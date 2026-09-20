using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autoroadmarking_Pro.Domain.Metadata;

namespace Autoroadmarking_Pro.CadHost.Cad.Metadata
{
    public sealed class EntityMetadataStore
    {
        private readonly XRecordService _xrecord = new XRecordService();
        private readonly JsonSerializerOptions _options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public ArmEntityMetadata? Read(Entity entity, Transaction tr)
        {
            if (entity.ExtensionDictionary.IsNull) return null;
            var dict = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead);
            string json = _xrecord.Read(dict, tr, ArmMetadataKeys.DictionaryName);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<ArmEntityMetadata>(json, _options); }
            catch { return null; }
        }

        public void Write(Entity entity, Transaction tr, ArmEntityMetadata metadata)
        {
            if (!entity.IsWriteEnabled) entity.UpgradeOpen();
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            var dict = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite);
            string json = JsonSerializer.Serialize(metadata, _options);
            _xrecord.Write(dict, tr, ArmMetadataKeys.DictionaryName, json);
        }

        public void Remove(Entity entity, Transaction tr)
        {
            if (entity.ExtensionDictionary.IsNull) return;
            var dict = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite);
            _xrecord.Remove(dict, tr, ArmMetadataKeys.DictionaryName);
        }
    }
}
