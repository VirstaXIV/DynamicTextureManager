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
    
    internal DTexture(DTextureBase other)
        : base(other)
    { }
    
    internal DTexture(DTexture other)
        : base(other)
    {
        Description            = other.Description;
    }
    
    public new const int FileVersion = 2;

    public Guid                         Identifier             { get; internal init; }
    public DateTimeOffset               CreationDate           { get; internal init; }
    public DateTimeOffset               LastEdit               { get; internal set; }
    public LowerString                  Name                   { get; internal set; } = LowerString.Empty;
    public string                       Description            { get; internal set; } = string.Empty;
    public int                          Index                  { get; internal set; }

    public string Incognito
        => Identifier.ToString()[..8];
    
    #endregion
    
    #region Serialization

    public new JObject JsonSerialize()
    {
        var ret = new JObject
        {
            ["FileVersion"]            = FileVersion,
            ["Identifier"]             = Identifier,
            ["CreationDate"]           = CreationDate,
            ["LastEdit"]               = LastEdit,
            ["Name"]                   = Name.Text,
            ["Description"]            = Description,
            ["WriteProtected"]         = WriteProtected(),
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
            Description  = json["Description"]?.ToObject<string>() ?? string.Empty,
            LastEdit     = json["LastEdit"]?.ToObject<DateTimeOffset>() ?? creationDate
        };
        
        if (dTexture.LastEdit < creationDate)
            dTexture.LastEdit = creationDate;
        
        dTexture.SetWriteProtected(json["WriteProtected"]?.ToObject<bool>() ?? false);
        // Version 1 files carry no payload and load with empty data.
        dTexture.SetDTextureData(DTextureData.Load(json["Data"] as JObject));
        return dTexture;
    }
    
    #endregion
    
    #region ISavable
    
    public string ToFilename(FilenameService fileNames)
        => fileNames.DTextureFile(this);

    public void Save(StreamWriter writer)
    {
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