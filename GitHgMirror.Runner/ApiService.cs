using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using GitHgMirror.CommonTypes;
using Newtonsoft.Json;

namespace GitHgMirror.Runner
{
    class ApiService
    {
        private readonly Settings _settings;


        public ApiService(Settings settings)
        {
            _settings = settings;
        }


        public T Get<T>(string path)
        {
            using (var wc = new WebClient())
            {
                // Setting UTF-8 is needed for accented characters to work properly.
                wc.Headers.Add(HttpRequestHeader.ContentType, "application/json; charset=utf-8");
                var apiUrl = _settings.ApiEndpointUrl.ToString();
                var url = apiUrl + (!apiUrl.EndsWith("/") ? "/" : "") + path.Trim('/');
                url += (url.Contains('?') ? "&" : "?") + "password=" + _settings.ApiPassword;
                return JsonConvert.DeserializeObject<T>(wc.DownloadString(url));
            }
        }
    }
}
