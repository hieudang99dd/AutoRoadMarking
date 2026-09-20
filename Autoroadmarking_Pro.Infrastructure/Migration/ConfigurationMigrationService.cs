using System;
using Autoroadmarking_Pro.Infrastructure.Configuration;

namespace Autoroadmarking_Pro.Infrastructure.Migration
{
    public sealed class ConfigurationMigrationService
    {
        public GlobalSettings Upgrade(GlobalSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (settings.SchemaVersion < SchemaVersion.Current)
                settings.SchemaVersion = SchemaVersion.Current;

            return settings;
        }
    }
}
