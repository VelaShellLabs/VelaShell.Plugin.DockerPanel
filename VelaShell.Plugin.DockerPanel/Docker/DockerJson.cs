using System.Text.Json;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// docker CLI 输出的解析。
/// <para>
/// 列表类命令一律用 <c>--format '{{json .}}'</c> 而不是 <c>--format json</c>:
/// 后者是 Docker 23 才有的整块 JSON,而前者从 1.x 起就在,输出是**每行一个对象**
/// 的 NDJSON。面板要能对着一台十年前装好的机器工作,就得挑那个老的。
/// </para>
/// <para>
/// 另一件必须容忍的事:docker 会把 <c>WARNING: ...</c>(如 "No swap limit support")
/// 混进标准错误,而我们把两条流合并了 —— 所以解析时**跳过一切不以 <c>{</c> 打头的行**,
/// 而不是遇到一行不认识就整批放弃。
/// </para>
/// </summary>
internal static class DockerJson
{
    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>把 NDJSON 输出解析成一串字段字典(值一律转成字符串)。</summary>
    /// <param name="output">docker 的原始输出。</param>
    /// <returns>逐行解析出的对象;整段无有效 JSON 时为空列表。</returns>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseLines(string output)
    {
        List<IReadOnlyDictionary<string, string>> rows = [];
        if (string.IsNullOrWhiteSpace(output))
        {
            return rows;
        }
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }
            if (TryParseObject(trimmed, out var row))
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>解析一整块 JSON 数组(<c>docker compose ls --format json</c> 这类)。</summary>
    /// <param name="output">docker 的原始输出。</param>
    /// <returns>数组里的每个对象;不是数组或解析失败时为空列表。</returns>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseArray(string output)
    {
        List<IReadOnlyDictionary<string, string>> rows = [];
        var start = output.IndexOf('[');
        if (start < 0)
        {
            // compose 的某些版本在 --format json 下回的仍是 NDJSON,退回逐行解析。
            return ParseLines(output);
        }
        var end = output.LastIndexOf(']');
        if (end <= start)
        {
            return rows;
        }
        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                return rows;
            }
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind is JsonValueKind.Object)
                {
                    rows.Add(ToRow(item));
                }
            }
        }
        catch (JsonException)
        {
            // 输出里混了别的东西(诊断行、进度条),按"这次没解析出来"处理。
        }
        return rows;
    }

    /// <summary>取一个字段;缺失或为空时回退。</summary>
    /// <param name="row">字段字典。</param>
    /// <param name="key">字段名。</param>
    /// <param name="fallback">回退值。</param>
    /// <returns>字段值。</returns>
    public static string Str(IReadOnlyDictionary<string, string> row, string key, string fallback = "") =>
        row.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    /// <summary>
    /// 从一段 JSON 对象文本里按路径取一个字符串属性。
    /// <para>
    /// 给嵌套对象用(<c>docker events</c> 的 <c>Actor.Attributes.name</c>)——
    /// <see cref="ParseLines" /> 把嵌套值原样留成 JSON 文本,这里再钻一层。
    /// 为一处嵌套引入一整套 POCO 反序列化不值当。
    /// </para>
    /// </summary>
    /// <param name="json">对象的 JSON 文本。</param>
    /// <param name="path">属性路径(逐层)。</param>
    /// <returns>属性值;任意一层缺失或不是字符串时返回空串。</returns>
    public static string Property(string json, params string[] path)
    {
        if (string.IsNullOrWhiteSpace(json) || path.Length == 0)
        {
            return string.Empty;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var current = document.RootElement;
            foreach (var segment in path)
            {
                if (current.ValueKind is not JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return string.Empty;
                }
            }
            return current.ValueKind is JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>把一段 JSON 重新格式化成缩进形式;解析不了就原样返回。</summary>
    /// <param name="json">原始 JSON。</param>
    /// <returns>可读的 JSON 文本。</returns>
    public static string Pretty(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }
        var start = json.IndexOfAny(['[', '{']);
        if (start < 0)
        {
            return json;
        }
        try
        {
            using var document = JsonDocument.Parse(json[start..]);
            return JsonSerializer.Serialize(document.RootElement, PrettyOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool TryParseObject(string line, out Dictionary<string, string> row)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind is JsonValueKind.Object)
            {
                row = ToRow(document.RootElement);
                return true;
            }
        }
        catch (JsonException)
        {
            // 半行输出(远端截断)不值得报错,跳过即可。
        }
        row = [];
        return false;
    }

    private static Dictionary<string, string> ToRow(JsonElement element)
    {
        Dictionary<string, string> row = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var property in element.EnumerateObject())
        {
            row[property.Name] = AsText(property.Value);
        }
        return row;
    }

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
        _ => value.ToString()
    };
}
