using System;
using System.IO;
using DynamicTextureManager.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OtterGui.Classes;

namespace DynamicTextureManager.DTextures;

public sealed class DTexture : DTextureBase, ISavable
{
    #region Data
    
    internal DTexture()
        : base()
    { }

    internal DTexture(DTexture other)
        : base(other)
    { }

    public const int FileVersion = 2;

    public Guid                         Identifier             { get; internal init; }
    public DateTimeOffset               CreationDate           { get; internal init; }
    public DateTimeOffset               LastEdit               { get; internal set; }
    public LowerString                  Name                   { get; internal set; } = LowerString.Empty;
    public int                          Index                  { get; internal set; }

    public string Incognito
        => Identifier.ToString()[..8];
    
    #endregion
    
    #region Serialization

    public JObject JsonSerialize()
    {
        var ret = new JObject
        {
            ["FileVersion"]            = FileVersion,
            ["Identifier"]             = Identifier,
            ["CreationDate"]           = CreationDate,
            ["LastEdit"]               = LastEdit,
            ["Name"]                   = Name.Text,
            ["Data"]                   = Data.Serialize()
        };
        return ret;
    }

    #endregion
    
    #region Deserialization
    
    public static DTexture LoadDTexture(JObject json)
    {
        var creationDate = json["CreationDate"]?.ToObject<DateTimeOffset>() ?? throw new ArgumentNullException("CreationDate");

        var dTexture = new DTexture()
        {
            CreationDate = creationDate,
            Identifier   = json["Identifier"]?.ToObject<Guid>() ?? throw new ArgumentNullException("Identifier"),
            Name         = new LowerString(json["Name"]?.ToObject<string>() ?? throw new ArgumentNullException("Name")),
            LastEdit     = json["LastEdit"]?.ToObject<DateTimeOffset>() ?? creationDate
        };
        
        if (dTexture.LastEdit < creationDate)
            dTexture.LastEdit = creationDate;

        // Version 1 files carry no payload and load with empty data.
        dTexture.SetDTextureData(DTextureData.Load(json["Data"] as JObject));
        return dTexture;
    }
    
    #endregion
    
    #region ISavable

    public string ToFilePath(FilenameService fileNames)
        => fileNames.DTextureFile(this);

    public void Save(Stream stream)
    {
        // UTF-8 with BOM keeps the on-disk format identical to earlier versions.
        using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        using var j = new JsonTextWriter(writer)
        {
            Formatting = Formatting.Indented,
        };
        var obj = JsonSerialize();
        obj.WriteTo(j);
    }

    public string LogName(string fileName)
        => Path.GetFileNameWithoutExtension(fileName);
    
    #endregion
}