using System;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace Autoroadmarking_Pro.CadHost.Cad.Layers
{
    /// <summary>
    /// Đồng bộ layer CAD từ Tab 1.
    /// Với layer đã tồn tại, không phá linetype tùy biến nếu tên linetype
    /// trong template chỉ là marker nghiệp vụ (TEMPLATE/BLOCK) hoặc không resolve được.
    /// Điều này đặc biệt quan trọng với vạch 7.3 đã được định nghĩa hình dạng trên layer.
    /// </summary>
    public sealed class CadLayerService
    {
        public ObjectId EnsureLayer(
            Database db,
            Transaction tr,
            string layerName,
            string linetypeName = "Continuous",
            string colorRgb = "255,255,255")
        {
            if (db == null)
                throw new ArgumentNullException(nameof(db));

            if (tr == null)
                throw new ArgumentNullException(nameof(tr));

            if (string.IsNullOrWhiteSpace(layerName))
                layerName = "0";

            Color color =
                ParseColor(
                    colorRgb);

            ObjectId requestedLinetype =
                TryResolveLinetype(
                    db,
                    tr,
                    linetypeName);

            LayerTable table =
                (LayerTable)tr.GetObject(
                    db.LayerTableId,
                    OpenMode.ForRead);

            if (table.Has(layerName))
            {
                LayerTableRecord existing =
                    (LayerTableRecord)tr.GetObject(
                        table[layerName],
                        OpenMode.ForWrite);

                existing.Color = color;

                // Chỉ ghi đè khi linetype thực sự resolve được.
                // Nếu template là TEMPLATE/BLOCK hoặc tên custom chưa load được,
                // giữ nguyên linetype hiện có của layer.
                if (!requestedLinetype.IsNull)
                {
                    existing.LinetypeObjectId =
                        requestedLinetype;
                }

                return existing.ObjectId;
            }

            table.UpgradeOpen();

            var record =
                new LayerTableRecord
                {
                    Name = layerName,
                    Color = color
                };

            // Layer mới phải có linetype hợp lệ.
            record.LinetypeObjectId =
                !requestedLinetype.IsNull
                    ? requestedLinetype
                    : db.ContinuousLinetype;

            ObjectId id =
                table.Add(
                    record);

            tr.AddNewlyCreatedDBObject(
                record,
                true);

            return id;
        }

        public ObjectId EnsureLayer(
            Database db,
            Transaction tr,
            string layerName,
            ObjectId linetypeId,
            string colorRgb = "255,255,255")
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (tr == null) throw new ArgumentNullException(nameof(tr));
            if (string.IsNullOrWhiteSpace(layerName)) layerName = "0";

            Color color = ParseColor(colorRgb);
            LayerTable table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            ObjectId effectiveLinetype = linetypeId.IsNull ? db.ContinuousLinetype : linetypeId;

            if (table.Has(layerName))
            {
                LayerTableRecord existing = (LayerTableRecord)tr.GetObject(table[layerName], OpenMode.ForWrite);
                existing.Color = color;
                existing.LinetypeObjectId = effectiveLinetype;
                return existing.ObjectId;
            }

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = layerName,
                Color = color,
                LinetypeObjectId = effectiveLinetype
            };

            ObjectId id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static ObjectId TryResolveLinetype(
            Database db,
            Transaction tr,
            string? requestedName)
        {
            string name =
                (requestedName ?? string.Empty)
                    .Trim();

            if (string.IsNullOrWhiteSpace(name))
                return ObjectId.Null;

            // Đây là marker nghiệp vụ, không phải tên linetype AutoCAD.
            if (name.Equals(
                    "TEMPLATE",
                    StringComparison.OrdinalIgnoreCase) ||
                name.Equals(
                    "BLOCK",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ObjectId.Null;
            }

            LinetypeTable table =
                (LinetypeTable)tr.GetObject(
                    db.LinetypeTableId,
                    OpenMode.ForRead);

            if (table.Has(name))
                return table[name];

            if (name.Equals(
                    "Continuous",
                    StringComparison.OrdinalIgnoreCase))
            {
                return db.ContinuousLinetype;
            }

            try
            {
                db.LoadLineTypeFile(
                    name,
                    "acadiso.lin");
            }
            catch
            {
            }

            table =
                (LinetypeTable)tr.GetObject(
                    db.LinetypeTableId,
                    OpenMode.ForRead);

            if (table.Has(name))
                return table[name];

            try
            {
                db.LoadLineTypeFile(
                    name,
                    "acad.lin");
            }
            catch
            {
            }

            table =
                (LinetypeTable)tr.GetObject(
                    db.LinetypeTableId,
                    OpenMode.ForRead);

            return table.Has(name)
                ? table[name]
                : ObjectId.Null;
        }

        private static Color ParseColor(
            string? rgb)
        {
            string value =
                (rgb ?? string.Empty)
                    .Trim();

            // Tab 1 có thể lưu màu bằng tên nghiệp vụ.
            if (value.Equals(
                    "TRẮNG",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "WHITE",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Color.FromRgb(
                    255,
                    255,
                    255);
            }

            if (value.Equals(
                    "VÀNG",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "YELLOW",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Color.FromRgb(
                    255,
                    255,
                    0);
            }

            if (value.StartsWith("#", StringComparison.Ordinal) && value.Length == 7)
            {
                if (byte.TryParse(value.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte hexRed) &&
                    byte.TryParse(value.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte hexGreen) &&
                    byte.TryParse(value.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte hexBlue))
                {
                    return Color.FromRgb(hexRed, hexGreen, hexBlue);
                }
            }

            string[] parts =
                value.Split(',');

            if (parts.Length == 3 &&
                byte.TryParse(
                    parts[0].Trim(),
                    out byte red) &&
                byte.TryParse(
                    parts[1].Trim(),
                    out byte green) &&
                byte.TryParse(
                    parts[2].Trim(),
                    out byte blue))
            {
                return Color.FromRgb(
                    red,
                    green,
                    blue);
            }

            return Color.FromRgb(
                255,
                255,
                255);
        }
    }
}
