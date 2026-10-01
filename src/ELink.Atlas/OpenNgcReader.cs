using Microsoft.Data.Sqlite;

namespace ELink.Atlas;

/// <summary>Reads the OpenNGC deep-sky catalogue (CC-BY-SA 4.0) from the SQLite file KStars installs (OpenNGC.kscat).</summary>
public static class OpenNgcReader
{
    // KStars SkyObject type codes
    public static string Kind(int type) => type switch
    {
        3 => "OpenCluster", 4 => "GlobularCluster", 5 => "Nebula", 6 => "PlanetaryNebula", 7 => "SupernovaRemnant",
        8 => "Galaxy", 13 => "Asterism", 14 => "GalaxyCluster", 15 => "DarkNebula", 17 => "MultipleStar", _ => "Other",
    };

    public static List<CatalogDso> Read(string file)
    {
        var list = new List<CatalogDso>();
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT type, ra, dec, magnitude, name, long_name, major_axis, minor_axis, position_angle FROM cat";
        using var r = cmd.ExecuteReader();
        float F(int i) => r.IsDBNull(i) ? float.NaN : (float)r.GetDouble(i);
        while (r.Read())
        {
            if (r.IsDBNull(1) || r.IsDBNull(2)) continue;
            int type = r.IsDBNull(0) ? 255 : r.GetInt32(0);
            string id = r.IsDBNull(4) ? "" : r.GetString(4);
            string common = r.IsDBNull(5) ? "" : r.GetString(5);
            float major = F(6), minor = F(7), pa = F(8);
            list.Add(new CatalogDso(id, Kind(type), common, r.GetDouble(1) / 15.0, r.GetDouble(2), F(3),
                float.IsNaN(major) ? 0 : major, float.IsNaN(minor) ? (float.IsNaN(major) ? 0 : major) : minor, float.IsNaN(pa) ? 0 : pa));
        }
        return list;
    }
}
