using System.Globalization;
using System.Text;

namespace SteamUnify;

internal sealed class LibrarySearch
{
    private readonly HashSet<int>? appIds;
    private readonly string[] terms;
    internal string Text { get; }
    internal bool IsActive => appIds!=null || terms.Length>0;

    internal LibrarySearch(string text)
    {
        Text=text;
        appIds=ReadIdList(text);
        terms=appIds==null ? text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries) : [];
    }

    internal bool Matches(GameRow row)
    {
        if(appIds!=null)return appIds.Contains(row.Id);
        foreach(string term in terms)
            if(!row.SearchText.Contains(term,StringComparison.OrdinalIgnoreCase))return false;
        return true;
    }

    private static bool Separator(char c) => char.IsWhiteSpace(c) || c is ',' or '，' or '、' or ';' or '；';

    private static HashSet<int>? ReadIdList(string text)
    {
        HashSet<int>? ids=null;
        int tokens=0;
        for(int pos=0;pos<text.Length;)
        {
            if(Separator(text[pos])) {pos++;continue;}
            int start=pos;
            while(pos<text.Length && char.IsAsciiDigit(text[pos]))pos++;
            if(pos==start || (pos<text.Length && !Separator(text[pos])))return null;
            tokens++;
            ids??=[];
            // Steam IDs are unsigned on disk. CacheGame represents those same 32 bits as an int.
            if(uint.TryParse(text.AsSpan(start,pos-start),NumberStyles.None,CultureInfo.InvariantCulture,out var id))
                ids.Add(unchecked((int)id));
        }
        return tokens>=2 ? ids : null;
    }

    internal static string? NormalizePaste(string current,int start,int length,string pasted)
    {
        if(!pasted.Any(c=>char.IsWhiteSpace(c) && c!=' '))return null;
        string combined=string.Concat(current.AsSpan(0,start),pasted.AsSpan(),current.AsSpan(start+length));
        if(ReadIdList(combined)==null)return null;
        var line=new StringBuilder(pasted.Length);
        foreach(char c in pasted)
        {
            if(!Separator(c))line.Append(c);
            else if(line.Length==0 || line[^1]!=' ')line.Append(' ');
        }
        return line.ToString();
    }
}
