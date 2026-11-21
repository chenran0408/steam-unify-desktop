using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SteamUnify;

public sealed partial class Suggestions
{
    public Suggestion[] RecommendBatch(IReadOnlyList<CacheGame> games,IReadOnlyDictionary<int,string> tagNames,
        IReadOnlyDictionary<int,string>? originalNames=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        var results = new Suggestion[games.Count];
        var preferences = new SavedApp[games.Count];
        lock (gate)
        {
            for (int i=0; i<games.Count; i++)
                preferences[i] = apps.GetValueOrDefault(games[i].AppId) ?? EmptyPreferences;
        }
        void Classify(int i)
        {
            ct.ThrowIfCancellationRequested();
            var game = games[i];
            results[i] = Recommend(game,tagNames,originalNames?.GetValueOrDefault(game.AppId),preferences[i]);
        }
        int workers = Math.Clamp(Environment.ProcessorCount-1,1,6);
        if (games.Count < 128 || workers == 1)
            for (int i=0; i<games.Count; i++) Classify(i);
        else Parallel.For(0,games.Count,new ParallelOptions { MaxDegreeOfParallelism=workers, CancellationToken=ct },Classify);
        return results;
    }

    public async Task LookupBatchAsync(IReadOnlyList<CacheGame> games,bool descriptions,string? proxy,
        Action<bool> onCompleted,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var finish = CheckpointProgress(onCompleted);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var options = LookupOptions(cancellation.Token);
        if (descriptions)
        {
            await Parallel.ForEachAsync(games,options,async (game,token) =>
                finish(await RetrieveDescriptionAsync(game,proxy,token).ConfigureAwait(false))).ConfigureAwait(false);
            return;
        }

        // Queue only a few fallbacks ahead. Steam store lookups and machine translations share one HTTP request limit.
        var fallback = Channel.CreateBounded<CacheGame>(2*SuggestionLookup.MaxRequests);
        async Task ReadStores()
        {
            try
            {
                await Parallel.ForEachAsync(games,options,async (game,token) =>
                {
                    var result = await LookupOfficialAsync(game,proxy,token).ConfigureAwait(false);
                    if (result.NeedsMachine)
                    {
                        await fallback.Writer.WriteAsync(game,token).ConfigureAwait(false);
                        return;
                    }
                    finish(result.Found);
                }).ConfigureAwait(false);
            }
            catch { cancellation.Cancel(); throw; }
            finally { fallback.Writer.TryComplete(); }
        }
        async Task TranslateMissing()
        {
            try
            {
                await Parallel.ForEachAsync(fallback.Reader.ReadAllAsync(cancellation.Token),options,(game,token) =>
                    TranslateNamesAsync(game.Name,[game],proxy,(_,found) => finish(found),token)).ConfigureAwait(false);
            }
            catch { cancellation.Cancel(); throw; }
        }
        await Task.WhenAll(ReadStores(),TranslateMissing()).ConfigureAwait(false);
    }

    public async Task<CacheGame[]> LookupOfficialBatchAsync(IReadOnlyList<CacheGame> games,string? proxy,
        Action<bool> onCompleted,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var unique = games.DistinctBy(game => game.AppId).ToArray();
        var missing = new bool[unique.Length];
        var finish = CheckpointProgress(onCompleted);
        await Parallel.ForEachAsync(Enumerable.Range(0,unique.Length),LookupOptions(ct),async (i,token) =>
        {
            var result = await LookupOfficialAsync(unique[i],proxy,token).ConfigureAwait(false);
            missing[i] = result.NeedsMachine;
            finish(result.Found);
        }).ConfigureAwait(false);
        return unique.Where((_,i) => missing[i]).ToArray();
    }

    public async Task LookupMachineBatchAsync(IReadOnlyList<CacheGame> games,string? proxy,
        Action<CacheGame,bool> onCompleted,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var byTitle = games.DistinctBy(game => game.AppId).GroupBy(game => game.Name,StringComparer.Ordinal);
        var checkpoint = NetworkCheckpoint();
        await Parallel.ForEachAsync(byTitle,LookupOptions(ct),(sameTitle,token) =>
            TranslateNamesAsync(sameTitle.Key,sameTitle,proxy,(game,found) =>
            {
                onCompleted(game,found);
                checkpoint();
            },token)).ConfigureAwait(false);
    }

    private static ParallelOptions LookupOptions(CancellationToken ct) =>
        new() { CancellationToken=ct, MaxDegreeOfParallelism=SuggestionLookup.MaxRequests };

    private Action<bool> CheckpointProgress(Action<bool> onCompleted)
    {
        var checkpoint = NetworkCheckpoint();
        return found => { onCompleted(found);checkpoint(); };
    }

    private Action NetworkCheckpoint()
    {
        int completed = 0;
        return () => { if (Interlocked.Increment(ref completed)%32 == 0) Save(); };
    }

    private async ValueTask TranslateNamesAsync(string title,IEnumerable<CacheGame> games,string? proxy,
        Action<CacheGame,bool> finish,CancellationToken ct)
    {
        var missing = new List<CacheGame>();
        foreach (var game in games)
        {
            ct.ThrowIfCancellationRequested();
            var state = LookupState(game);
            if (state.NeedsMachine) missing.Add(game);
            else finish(game,state.Found);
        }
        if (missing.Count == 0) return;
        var result = await network.ResolveMachineAsync(title,proxy,ct).ConfigureAwait(false);
        foreach (var game in missing)
        {
            ct.ThrowIfCancellationRequested();
            RememberMachineResult(game,result);
            finish(game,LookupState(game).Found);
        }
    }
}
