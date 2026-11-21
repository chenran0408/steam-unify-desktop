using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SteamUnify;

internal static class BatchChecks
{
    static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
    static void Maximum(ref int target,int value)
    {
        int current;
        while ((current=Volatile.Read(ref target))<value && Interlocked.CompareExchange(ref target,value,current)!=current) { }
    }

    // Instrument only the analysis pass, after the one-time preference snapshot.
    sealed class ObservedList(CacheGame[] input,Action onClassify) : IReadOnlyList<CacheGame>
    {
        int reads,active,max;
        public int Count => input.Length;
        public int Max => max;
        public CacheGame this[int index]
        {
            get
            {
                if (Interlocked.Increment(ref reads)>Count)
                {
                    Maximum(ref max,Interlocked.Increment(ref active));
                    try { onClassify(); } finally { Interlocked.Decrement(ref active); }
                }
                return input[index];
            }
        }
        public IEnumerator<CacheGame> GetEnumerator() => ((IEnumerable<CacheGame>)input).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static async Task Run(string scratch,bool benchmark)
    {
        var tags = new Dictionary<int,string>{{11,"第一人称射击"},{12,"角色扮演"},{13,"生存"},{14,"沙盒"},{15,"开放世界生存制作"}};
        var library = Enumerable.Range(0,6000).Select(i=>new CacheGame(17000000+i, $"Library Test {i}", "game",
            i%3==0 ? new(){{"schinese",$"内置译名 {i}"}} : new(),
            i%2==0 ? [1,3] : [25], i%2==0 ? [11,12] : [13,14,15])).ToArray();
        var originals = library.Where(g=>g.AppId%4==0).ToDictionary(g=>g.AppId,g=>"Sandbox "+g.AppId);
        var engine = new Suggestions(Path.Combine(scratch,"analysis"));
        for (int i=0;i<80;i+=5) engine.SetManual(library[i].AppId,"个人命名 "+i,"收藏 "+i);
        Suggestion[] Serial(CacheGame[] source) => source.Select(g=>engine.Recommend(g,tags,originals.GetValueOrDefault(g.AppId))).ToArray();
        void Same(IReadOnlyList<Suggestion> expected,IReadOnlyList<Suggestion> actual)
        {
            Check(expected.Count==actual.Count,"analysis row count");
            for (int i=0;i<expected.Count;i++)
            {
                var old=expected[i]; var now=actual[i];
                Check(old==now with {Tags=old.Tags} && old.Tags.SequenceEqual(now.Tags),"analysis order/values including tags at "+i);
            }
        }
        var small = new ObservedList(library[..12],()=>{});
        Same(Serial(library[..12]),engine.RecommendBatch(small,tags,originals));
        var larger = new ObservedList(library[..400],()=>Thread.Sleep(1));
        Same(Serial(library[..400]),engine.RecommendBatch(larger,tags,originals));
        Check(small.Max==1 && larger.Max<=6 && (Environment.ProcessorCount<=2 || larger.Max>1),"bounded classification workers");
        using var cancel=new CancellationTokenSource();
        try
        {
            engine.RecommendBatch(new ObservedList(library,cancel.Cancel),tags,originals,cancel.Token);
            throw new Exception("classification cancellation ignored");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        if (benchmark)
        {
            static double Measure(Action work) {var t=Stopwatch.StartNew();work();return t.Elapsed.TotalMilliseconds;}
            Same(Serial(library),engine.RecommendBatch(library,tags,originals));
            double[] sequential=new double[7],parallel=new double[7];
            for (int i=0;i<sequential.Length;i++)
            {
                if (i%2==0) sequential[i]=Measure(()=>Serial(library));
                parallel[i]=Measure(()=>engine.RecommendBatch(library,tags,originals));
                if (i%2!=0) sequential[i]=Measure(()=>Serial(library));
            }
            Array.Sort(sequential);Array.Sort(parallel);
            Console.WriteLine($"6,000 games, median of 7 warm runs: serial {sequential[3]:F1} ms, batch {parallel[3]:F1} ms, {sequential[3]/parallel[3]:F2}x; small/large observed workers {small.Max}/{larger.Max}.");
        }

        async Task<(double FirstStore,int Peak)> NetworkBatch(bool staged)
        {
            var games=library.Take(38).Select(g=>g with {Localized=new()}).ToArray();
            int active=0,peak=0,completed=0,storeCount=0;
            var starts=new ConcurrentQueue<long>();
            var clock=Stopwatch.StartNew();
            long firstStore=-1;
            var storeSeen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lookup=new SuggestionLookup(async (uri,proxy,ct)=>
            {
                Maximum(ref peak,Interlocked.Increment(ref active));
                try
                {
                    if (uri.Host.StartsWith("api.mymemory"))
                    {
                        starts.Enqueue(Stopwatch.GetTimestamp());
                        // During the ordinary test a slow machine provider waits for all later Steam lookups.
                        if (!benchmark) await storeSeen.Task.WaitAsync(ct);
                        await Task.Delay(180,ct);
                        return JsonSerializer.Deserialize<JsonElement>("""{"responseData":{"translatedText":"模拟机翻"}}""");
                    }
                    await Task.Delay(12,ct);
                    int id=int.Parse(uri.Query.Split("appids=")[1].Split('&')[0]);
                    if (id-games[0].AppId<4) return JsonSerializer.Deserialize<JsonElement>($"{{\"{id}\":{{\"success\":false}}}}");
                    Interlocked.CompareExchange(ref firstStore,clock.ElapsedMilliseconds,-1);
                    if (Interlocked.Increment(ref storeCount)==games.Length-4) storeSeen.TrySetResult();
                    return JsonSerializer.Deserialize<JsonElement>($"{{\"{id}\":{{\"success\":true,\"data\":{{\"name\":\"商店译名 {id}\",\"short_description\":\"简介\"}}}}}}");
                }
                finally { Interlocked.Decrement(ref active); }
            });
            string folder=Path.Combine(scratch,staged?"staged":"legacy-scheduler");
            var online=new Suggestions(folder,lookup);
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (staged)
                await online.LookupBatchAsync(games,false,null,found=>{Check(found,"network result");Interlocked.Increment(ref completed);},limit.Token);
            else
                await Parallel.ForEachAsync(games,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=limit.Token},async (g,ct)=>{
                    Check(await online.LookupAsync(g,null,ct)!=null,"single lookup");Interlocked.Increment(ref completed);
                });
            Check(completed==games.Length && peak is >1 and <=4 && storeCount==games.Length-4,"bounded HTTP and exact progress");
            Check(online.Recommend(games[0],tags).Source=="机器翻译" && online.Recommend(games[4],tags).Source=="Steam 商店","sources preserved");
            long[] machine=starts.ToArray();
            Check(machine.Length==4 && machine.Zip(machine.Skip(1),(a,b)=>Stopwatch.GetElapsedTime(a,b).TotalMilliseconds).All(ms=>ms>=560),"global machine pacing");
            if (staged)
            {
                var persisted=new Suggestions(folder,lookup);
                Check(games.Count(g=>persisted.Recommend(g,tags).Name.Length>0)>=32,"checkpoint during batch");
                online.Save();
            }
            return(firstStore,peak);
        }
        var modern=await NetworkBatch(true);
        if (benchmark)
        {
            await Task.Delay(650);
            var previous=await NetworkBatch(false);
            Console.WriteLine($"Controlled store 12 ms and machine 180 ms: first official result {previous.FirstStore:F0} ms with four whole-game workers, {modern.FirstStore:F0} ms with staged scheduling. Peak HTTP {modern.Peak}/4.");
        }
        using var stopping=new CancellationTokenSource();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled=new Suggestions(Path.Combine(scratch,"stop"),new SuggestionLookup(async (_,_,ct)=>{
            started.TrySetResult();await Task.Delay(Timeout.Infinite,ct);return null;
        }));
        var pending=stalled.LookupBatchAsync(library.Where(g=>g.Localized.Count==0).Take(30).ToArray(),false,null,_=>{},stopping.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));stopping.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(4));throw new Exception("network cancellation ignored"); }
        catch(OperationCanceledException) when(stopping.IsCancellationRequested) { }
        await TranslationPhases(scratch,library.Where(g=>g.Localized.Count==0).Take(16).ToArray(),tags,benchmark);
    }

    private static async Task TranslationPhases(string scratch,CacheGame[] games,
        IReadOnlyDictionary<int,string> tags,bool benchmark)
    {
        var officiallyNamed=games.Skip(4).Select(g=>g.AppId).ToHashSet();
        var firstId=games[0].AppId;
        var lastManual=games[1];
        var completedMachine=new ConcurrentDictionary<int,bool>();
        int machine=0,store=0,peak=0,active=0,officialReady=0;
        bool oldPath=false;
        Suggestions? online=null;
        var lookup=new SuggestionLookup(async (uri,_,ct)=>
        {
            Maximum(ref peak,Interlocked.Increment(ref active));
            try
            {
                if (uri.Host.StartsWith("api.mymemory"))
                {
                    Interlocked.Increment(ref machine);
                    if (!benchmark && !oldPath) online!.SetManual(lastManual.AppId,"刚选的中文名",null);
                    await Task.Delay(180,ct);
                    return JsonSerializer.Deserialize<JsonElement>("""{"responseData":{"translatedText":"模拟机翻"}}""");
                }
                Interlocked.Increment(ref store);
                await Task.Delay(75,ct);
                int id=int.Parse(uri.Query.Split("appids=")[1].Split('&')[0]);
                string name=officiallyNamed.Contains(id) ? $"商店译名 {id}" : "Original name";
                return JsonSerializer.Deserialize<JsonElement>($"{{\"{id}\":{{\"success\":true,\"data\":{{\"name\":\"{name}\",\"short_description\":\"官方简介\"}}}}}}");
            }
            finally { Interlocked.Decrement(ref active); }
        });
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(12));
        if (!benchmark)
            games[1]=games[1] with {Name=games[0].Name};
        string folder=Path.Combine(scratch,"official-first");
        online=new Suggestions(folder,lookup);
        if (!benchmark) online.SetManual(games[2].AppId,"保留译名",null);
        var clock=Stopwatch.StartNew();
        var unresolved=await online.LookupOfficialBatchAsync([..games,games[0]],null,
            found=>{if(found)Interlocked.Increment(ref officialReady);},limit.Token);
        var officialMs=clock.Elapsed.TotalMilliseconds;
        Check(machine==0 && officialReady==officiallyNamed.Count+(benchmark?0:1),"only official/local names reported in phase one");
        Check(unresolved.Select(x=>x.AppId).SequenceEqual(games.Take(4).Where(g=>benchmark || g.AppId!=games[2].AppId).Select(x=>x.AppId)),
            "official pass returns exactly the still-missing games in input order");
        online.Save();
        using (var data=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder,"suggestions.v2.json"))))
            Check(data.RootElement.GetProperty(firstId.ToString()).GetProperty("Description").GetString()=="官方简介",
                "unresolved name retains official description before any machine work");
        int beforeMachine=store;
        await online.LookupMachineBatchAsync(unresolved,null,(g,found)=>
            Check(completedMachine.TryAdd(g.AppId,found),"machine progress duplicated an AppID"),limit.Token);
        var totalMs=clock.Elapsed.TotalMilliseconds;
        Check(store==beforeMachine && completedMachine.Count==unresolved.Length && completedMachine.Values.All(found=>found),
            "machine phase performs no redundant Steam requests and reports every game");
        Check(machine==(benchmark?4:2) && peak is >1 and <=SuggestionLookup.MaxRequests,"deduplicated titles and global request ceiling");
        if (!benchmark)
        {
            Check(online.Recommend(games[0],tags).Source=="机器翻译" &&
                online.Recommend(games[1],tags).Name=="刚选的中文名" &&
                online.Recommend(games[2],tags).Name=="保留译名","pending machine result preserves edits");
            var cancelledLookup=new SuggestionLookup(async (_,_,ct)=>
            {
                await Task.Delay(Timeout.Infinite,ct);
                return null;
            });
            var cancelled=new Suggestions(Path.Combine(scratch,"phase-stop"),cancelledLookup);
            foreach (bool cancelOfficial in new[]{true,false})
            {
                using var stopping=new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
                Task cancelledRun=cancelOfficial
                    ? cancelled.LookupOfficialBatchAsync(games,null,_=>{},stopping.Token)
                    : cancelled.LookupMachineBatchAsync(games,null,(_,_)=>{},stopping.Token);
                try { await cancelledRun.WaitAsync(TimeSpan.FromSeconds(3));throw new Exception("translation phase ignored cancellation"); }
                catch(OperationCanceledException) when(stopping.IsCancellationRequested) { }
            }
            return;
        }

        // 2.6 awaits the pipelined batch before its caller can submit any change.
        await Task.Delay(650,limit.Token);
        oldPath=true;
        machine=store=peak=0;
        var previous=new Suggestions(Path.Combine(scratch,"combined-26"),lookup);
        clock.Restart();
        await previous.LookupBatchAsync(games,false,null,_=>{},limit.Token);
        var previousMs=clock.Elapsed.TotalMilliseconds;
        Check(previous.RecommendBatch(games,tags).Zip(online.RecommendBatch(games,tags),(a,b)=>
            a==b with {Tags=a.Tags} && a.Tags.SequenceEqual(b.Tags)).All(same=>same),"two phases reproduce 2.6 translation sources and descriptions");
        Check(machine==unresolved.Length && store==beforeMachine && peak<=SuggestionLookup.MaxRequests,"same workload and global ceiling in 2.6 and the new flow");
        Console.WriteLine($"16 games, fixed 75 ms/request Steam and 180 ms/request machine (12 official, 4 fallbacks, no write time): " +
            $"2.6 could submit official results at {previousMs:F0} ms and all candidates at {previousMs:F0} ms; " +
            $"official-first could submit at {officialMs:F0} ms and complete all candidates at {totalMs:F0} ms.");
    }
}
