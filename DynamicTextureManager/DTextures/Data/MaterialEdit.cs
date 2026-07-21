using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynamicTextureManager.DTextures.Data;

/// <summary> All colorset edits for one material, keyed by its game path in <see cref="DTextureData.Materials"/>. </summary>
public sealed class MaterialEdit
{
    /// <summary> The shader package name captured when the material was first edited, used to pick the shader handler. </summary>
    public string ShaderName = string.Empty;

    /// <summary> Whether the source material used a legacy 16-row table when captured. </summary>
    public bool Legacy;

    /// <summary> Edited rows, keyed by row index. </summary>
    public Dictionary<int, ColorRowEdit> Rows = [];

    public bool IsEmpty
        => Rows.Count == 0;

    public JObject Serialize()
        => new()
        {
            ["ShaderName"] = ShaderName,
            ["Legacy"]     = Legacy,
            ["Rows"]       = new JArray(Rows.Values.OrderBy(r => r.RowIndex).Select(r => r.Serialize())),
        };

    public static MaterialEdit Load(JObject json)
    {
        var ret = new MaterialEdit
        {
            ShaderName = json["ShaderName"]?.ToObject<string>() ?? string.Empty,
            Legacy     = json["Legacy"]?.ToObject<bool>() ?? false,
        };
        if (json["Rows"] is JArray rows)
            foreach (var row in rows.OfType<JObject>().Select(ColorRowEdit.Load))
                ret.Rows[row.RowIndex] = row;
        return ret;
    }
}
