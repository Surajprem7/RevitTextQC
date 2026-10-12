using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitTextQC.Core
{
    /// <summary>The fields of a GitHub release that the updater needs.</summary>
    [DataContract]
    public class ReleaseInfo
    {
        [DataMember(Name = "tag_name")] public string TagName { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "body")] public string Notes { get; set; }
        [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "assets")] public List<ReleaseAsset> Assets { get; set; }

        public Version Version => VersionUtil.Parse(TagName);

        public ReleaseAsset FindAsset(string fileName)
        {
            return (Assets ?? new List<ReleaseAsset>())
                .FirstOrDefault(a => string.Equals(a.Name, fileName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Parses the JSON returned by GET /repos/{owner}/{repo}/releases/latest.</summary>
        public static ReleaseInfo Parse(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (ReleaseInfo)new DataContractJsonSerializer(typeof(ReleaseInfo)).ReadObject(stream);
            }
        }
    }

    [DataContract]
    public class ReleaseAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        /// <summary>"sha256:..." when GitHub provides it.</summary>
        [DataMember(Name = "digest")] public string Digest { get; set; }

        public string Sha256 =>
            Digest != null && Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? Digest.Substring("sha256:".Length)
                : null;
    }

    public static class VersionUtil
    {
        /// <summary>Turns "v1.2.3" or "1.2.3-beta" into a comparable version; null when it is not a version.</summary>
        public static Version Parse(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string text = tag.Trim().TrimStart('v', 'V');
            int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) text = text.Substring(0, cut);
            if (!text.Contains(".")) text += ".0";
            return Version.TryParse(text, out var version) ? Normalize(version) : null;
        }

        public static bool IsNewer(Version latest, Version current)
        {
            if (latest == null || current == null) return false;
            return Normalize(latest) > Normalize(current);
        }

        // 1.2 and 1.2.0.0 must compare equal; Version treats missing parts as -1.
        private static Version Normalize(Version v)
        {
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }
    }
}
