using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SteamUnify;

// Compiled only into the isolated acceptance host. The window uses the ordinary suggestions workflow.
public partial class MainWindow
{
    internal MainWindow(string directory,SuggestionLookup fixture):this(directory)
    {
        suggestions=new Suggestions(directory,fixture);
    }
    internal async Task VerifyFixturePasteAsync()
    {
        var timeout=System.Diagnostics.Stopwatch.StartNew();
        while((busy || rows.Count!=8) && timeout.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(30);
        if(busy || rows.Count!=8)throw new InvalidOperationException("Fixture library did not load before paste.");
        var nativePaste=typeof(TextBox).Assembly.GetType("System.Windows.Documents.TextEditorCopyPaste")!
            .GetMethod("_DoPaste",BindingFlags.NonPublic|BindingFlags.Static)!;
        var editor=typeof(TextBoxBase).GetProperty("TextEditor",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(Search);
        var data=new DataObject(DataFormats.UnicodeText,"9001101\r\n9001104\n646570");
        try
        {
            if(nativePaste.Invoke(null,[editor,data,false]) is not true)throw new InvalidOperationException("WPF paste was not applied.");
            FlushSearch();
            if(Search.Text!="9001101 9001104 646570" || !Visible().Select(row=>row.Id).ToHashSet().SetEquals([9001101,9001104,646570]))
                throw new InvalidOperationException("Multiline paste did not preserve all three exact AppIDs.");
        }
        finally{Search.Clear();FlushSearch();}
    }
}

internal sealed class ReplayHost:App
{
    [STAThread]
    public static new void Main()
    {
        var app=new ReplayHost();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"data");
        async Task<JsonElement?> Respond(Uri uri,string? proxy,CancellationToken ct)
        {
            // Store lookups miss in this isolated corpus. Only explicitly released machine responses succeed.
            if(uri.Host!="api.mymemory.translated.net")return null;
            string query=Uri.UnescapeDataString(uri.Query);
            var fixture=query.Contains("Amber Archive",StringComparison.Ordinal)?("amber","琥珀档案"):
                query.Contains("Cinder Archive",StringComparison.Ordinal)?("cinder","余烬档案"):
                throw new InvalidOperationException("Unexpected fixture machine request: "+query);
            File.WriteAllText(Path.Combine(directory,fixture.Item1+".started"),"");
            while(!File.Exists(Path.Combine(directory,fixture.Item1+".release")))await Task.Delay(25,ct);
            return JsonSerializer.SerializeToElement(new {responseStatus=200,responseData=new {translatedText=fixture.Item2}});
        }
        var window=new MainWindow(directory,new SuggestionLookup(Respond));
        MainWindow=window;
        window.Show();
        try
        {
            await window.VerifyFixturePasteAsync();
            File.WriteAllText(Path.Combine(directory,"paste.result"),"passed");
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"paste.result"),ex.ToString());}
    }
}
