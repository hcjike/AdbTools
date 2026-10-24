using System.Collections.Generic;

namespace AdbTools.bean
{
    public class GithubReleases
    {
        public long Id { get; set; }

        /// <summary>
        /// 发布的名称
        /// </summary>
        public string tag_name { get; set; }

        /// <summary>
        /// 上传构件
        /// </summary>
        public List<GithubReleasesAssets> Assets { get; set; }

        /// <summary>
        /// 发布的说明，Markdown
        /// </summary>
        public string Body { get; set; }

        public string created_at { get; set; }

    }
}
