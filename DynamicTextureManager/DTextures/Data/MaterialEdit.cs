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

    /// <summary>
    /// 1-based colorset slots (pairs) the user marked as usable for decals even though the
    /// id map references them. The scanner blocks a whole slot over a single referencing
    /// texel, so junk pixels in modded maps can lock out slots that are effectively free —
    /// this override hands them back to the allocator at the user's own judgment.
    /// </summary>
    public List<int> UsableSlots = [];

    public bool IsEmpty
        => Rows.Count == 0 && UsableSlots.Count == 0;

    public JObject Serialize()
        => new()
        {
            ["ShaderName"]  = ShaderName,
            ["Legacy"]      = Legacy,
            ["Rows"]        = new JArray(Rows.Values.OrderBy(r => r.RowIndex).Select(r => r.Serialize())),
            ["UsableSlots"] = new JArray(UsableSlots),
        };

    public static MaterialEdit Load(JObject json)
    {
        var ret = new MaterialEdit
        {
            ShaderName  = json["ShaderName"]?.ToObject<string>() ?? string.Empty,
            Legacy      = json["Legacy"]?.ToObject<bool>() ?? false,
            UsableSlots = json["UsableSlots"]?.ToObject<List<int>>() ?? [],
        };
        if (json["Rows"] is JArray rows)
            foreach (var row in rows.OfType<JObject>().Select(ColorRowEdit.Load))
                ret.Rows[row.RowIndex] = row;
        return ret;
    }
}
