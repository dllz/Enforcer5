using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Enforcer5
{
    public class Language
    {
        public string Name { get; set; }
        public string Base { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public XDocument Doc { get; set; }
        public DateTime LatestUpdate { get; }
        public string IEFT { get; set; }

        public Language(string path)
        {
            Doc = XDocument.Load(path);
            Name = Doc.Descendants("language").First().Attribute("name")?.Value;
            Base = Doc.Descendants("language").First().Attribute("base")?.Value;
            IEFT = Doc.Descendants("language").First().Attribute("IEFT")?.Value.ToLower();
            FilePath = path;
            FileName = Path.GetFileNameWithoutExtension(path);
            LatestUpdate = File.GetLastWriteTimeUtc(path);
        }
    }
}
