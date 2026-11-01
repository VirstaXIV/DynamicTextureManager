using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using DynamicTextureManager.DTextures.Data;
using DynamicTextureManager.Interop;
using IService = Luna.IService;
using Penumbra.GameData.Files;

namespace DynamicTextureManager.ModGeneration;

// Face canvas assembly: the face model of a face skin material, aligned into the
// body race's space via the racial pre-bone deformer so world-space patterns agree.
public sealed partial class ModelUvReader
{
    /// <summary>
    /// The face model's geometry for a face skin material: model path derived from the
    /// material path (chara/human/cX/obj/face/fY/model/cXfY_fac.mdl), only the source
    /// material's own meshes editable — the face model also carries iris/brow/occlusion
    /// meshes whose UVs live in other textures.
    /// </summary>
    private MaterialMesh? GetFaceMesh(SourcePath source)
    {
        var match = FaceSkinMaterialPattern.Match(source.GamePath);
        if (!match.Success)
            return null;

        var race      = match.Groups[1].Value;
        var face      = match.Groups[2].Value;
        var bodyRace  = BodyTopModelPattern.Match(source.MdlGamePath) is { Success: true } body ? body.Groups[1].Value : race;
        var modelPath = $"chara/human/{race}/obj/face/{face}/model/{race}{face}_fac.mdl";
        var resolved  = penumbra.ResolvePlayerPath(modelPath);
        var key       = $"face|{source.GamePath}|{resolved}|{bodyRace}";
        if (_meshCache.TryGetValue(key, out var cached))
            return cached;

        MaterialMesh? mesh = null;
        try
        {
            var bytes = LoadGameFile(modelPath);
            if (bytes == null)
            {
                DynamicTextureManager.Log.Warning($"Could not load face model {modelPath} for its geometry.");
            }
            else
            {
                var fileName = Path.GetFileName(source.GamePath);
                mesh = MdlMeshReader.ReadMeshes([new MdlFile(bytes)],
                    material => material.EndsWith(fileName, StringComparison.OrdinalIgnoreCase), fileName, modelPath);
                if (mesh != null)
                {
                    if (!string.Equals(race, bodyRace, StringComparison.OrdinalIgnoreCase))
                        ApplyRacialHeadAlignment(mesh, race, bodyRace);
                    DynamicTextureManager.Log.Information(
                        $"Face geometry of {source.GamePath}: {mesh.VertexCount} vertices, {mesh.TriangleCount} triangles.");
                }
            }
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not read face geometry for {source.GamePath}: {ex.Message}");
        }

        _meshCache[key] = mesh;
        return mesh;
    }

    private PbdFile? _pbd;
    private bool     _pbdLoaded;

    /// <summary>
    /// A face authored for one race sitting on a body of another (the face path carries the
    /// character's true race, body models resolve through the body race): raw model spaces
    /// differ by the racial pre-bone deformer the game applies at runtime — the face sat "a
    /// bit forward and down" of the neck. Apply the head bone's deform inverse, mapping the
    /// face into the body's raw space, so preview and world-space pattern agree with the
    /// body. Single-bone (j_kao) is a rigid approximation — exactly the offset in question.
    /// </summary>
    private void ApplyRacialHeadAlignment(MaterialMesh mesh, string faceRace, string bodyRace)
    {
        try
        {
            if (!_pbdLoaded)
            {
                _pbdLoaded = true;
                var bytes = LoadGameFile("chara/xls/boneDeformer/human.pbd");
                if (bytes != null)
                    _pbd = new PbdFile(bytes);
            }

            if (_pbd == null)
                return;

            // The well-defined deform chain runs base-model → derived-skeleton (the game
            // deforms c0201-authored bodies onto a c0801 skeleton); the face already lives
            // in the derived space, so its inverse brings it back to the body's raw space.
            // The NECK bone governs the junction ring on both sides — the head bone's deform
            // overshoots by its extra head-relative motion (verified ~1.5 cm too high).
            var deformer = _pbd.GetRacialDeformer(
                (Penumbra.GameData.Enums.GenderRace)int.Parse(faceRace[1..]),
                (Penumbra.GameData.Enums.GenderRace)int.Parse(bodyRace[1..]));
            if (!deformer.DeformMatrices.TryGetValue("j_kubi", out var matrix)
             && !deformer.DeformMatrices.TryGetValue("j_kao", out matrix))
                return;

            var inverse = matrix.Invert();
            for (var v = 0; v < mesh.Positions.Length; ++v)
            {
                mesh.Positions[v] = inverse.Apply(mesh.Positions[v]);
                var normal = inverse.Apply(mesh.Normals[v]) - inverse.Translation;
                if (normal.LengthSquared() > 1e-12f)
                    mesh.Normals[v] = Vector3.Normalize(normal);
            }

            DynamicTextureManager.Log.Information(
                $"Face aligned into the body's raw space via the racial head deformer ({faceRace} -> {bodyRace}).");
        }
        catch (Exception ex)
        {
            DynamicTextureManager.Log.Warning($"Could not align the face to the body race: {ex.Message}");
        }
    }
}
