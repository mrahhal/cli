// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Newtonsoft.Json.Linq;
using NuGet.Protocol;
using Task = Microsoft.Build.Utilities.Task;

namespace Microsoft.DotNet.Cli.Build
{
    public class UploadToLinuxPackageRepository : Task
    {
        /// <summary>
        ///     The Azure repository service user name.
        /// </summary>
        [Required]
        public string Username { get; set; }

        /// <summary>
        ///     The Azure repository service Password.
        /// </summary>
        [Required]
        public string Password { get; set; }

        /// <summary>
        ///     The Azure repository service URL ex: "tux-devrepo.corp.microsoft.com".
        /// </summary>
        [Required]
        public string Server { get; set; }

        [Required]
        public string RepositoryId { get; set; }

        [Required]
        public string PathOfPackageToUpload { get; set; }

        [Required]
        public string PackageNameInLinuxPackageRepository { get; set; }


        [Required]
        public string PackageVersionInLinuxPackageRepository { get; set; }


        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        public async Task<bool> ExecuteAsync()
        {
            var linuxPackageRepositoryDestiny =
                new LinuxPackageRepositoryDestiny(Username, Password, Server, RepositoryId);
            var uploadResponse = await new LinuxPackageRepositoryHttpPrepare(
                linuxPackageRepositoryDestiny,
                new FileUploadStrategy(PathOfPackageToUpload)).RemoteCall();

            var idInRepositoryService = new IdInRepositoryService(JObject.Parse(uploadResponse)["id"].ToString());

            var addPackageResponse = await new LinuxPackageRepositoryHttpPrepare(
                linuxPackageRepositoryDestiny,
                new AddPackageStrategy(
                    idInRepositoryService,
                    PackageNameInLinuxPackageRepository,
                    PackageVersionInLinuxPackageRepository,
                    linuxPackageRepositoryDestiny.RepositoryId)).RemoteCall();

            var queueResourceLocation = new QueueResourceLocation(addPackageResponse);

            Func<Task<string>> pullQueuedPackageStatus = new LinuxPackageRepositoryHttpPrepare(
                linuxPackageRepositoryDestiny,
                new PullQueuedPackageStatus(queueResourceLocation)).RemoteCall;

            ExponentialRetry.ExecuteWithRetry(
                pullQueuedPackageStatus,
                s => s == "fileReady",
                5,
                () => ExponentialRetry.Timer(ExponentialRetry.Intervals),
                "testing retry").Wait();
            return true;
        }
    }

    public class LinuxPackageRepositoryDestiny
    {
        private readonly string _password;
        private readonly string _server;
        private readonly string _username;

        public LinuxPackageRepositoryDestiny(string username,
            string password,
            string server,
            string repositoryId)
        {
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            RepositoryId = repositoryId ?? throw new ArgumentNullException(nameof(repositoryId));
        }

        public string RepositoryId { get; }

        public Uri GetBaseAddress()
        {
            return new Uri($"https://{_server}");
        }

        public string GetSimpleAuth()
        {
            return $"{_username}:{_password}";
        }
    }

    public class LinuxPackageRepositoryHttpPrepare
    {
        private readonly IAzurelinuxRepositoryServiceHttpStrategy _httpStrategy;
        private readonly LinuxPackageRepositoryDestiny _linuxPackageRepositoryDestiny;

        public LinuxPackageRepositoryHttpPrepare(
            LinuxPackageRepositoryDestiny linuxPackageRepositoryDestiny,
            IAzurelinuxRepositoryServiceHttpStrategy httpStrategy
        )
        {
            _linuxPackageRepositoryDestiny = linuxPackageRepositoryDestiny
                                             ?? throw new ArgumentNullException(nameof(linuxPackageRepositoryDestiny));
            _httpStrategy = httpStrategy ?? throw new ArgumentNullException(nameof(httpStrategy));
        }

        public async Task<string> RemoteCall()
        {
            using (var handler = new HttpClientHandler())
            {
                using (var client = new HttpClient(handler))
                {
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                    var authHeader =
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(_linuxPackageRepositoryDestiny.GetSimpleAuth()));
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Basic", authHeader);
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    client.Timeout = TimeSpan.FromMinutes(10);

                    return await _httpStrategy.Execute(client, _linuxPackageRepositoryDestiny.GetBaseAddress());
                }
            }
        }
    }

    public class FileUploadStrategy : IAzurelinuxRepositoryServiceHttpStrategy
    {
        private readonly string _pathToPackageToUpload;

        public FileUploadStrategy(string pathToPackageToUpload)
        {
            _pathToPackageToUpload = pathToPackageToUpload
                                     ?? throw new ArgumentNullException(nameof(pathToPackageToUpload));
        }

        public async Task<string> Execute(HttpClient client, Uri baseAddress)
        {
            var fileName = Path.GetFileName(_pathToPackageToUpload);

            using (var content =
                new MultipartFormDataContent())
            {
                var url = new Uri(baseAddress, "/v1/files");
                content.Add(
                    new StreamContent(
                        new MemoryStream(
                            File.ReadAllBytes(_pathToPackageToUpload))),
                    "file",
                    fileName);
                using (var message = await client.PostAsync(url, content))
                {
                    if (!message.IsSuccessStatusCode)
                    {
                        throw new FailedToAddPackageToPackageRepositoryException(
                            $"{message.ToJson()} failed to post file to {url} file name:{fileName} pathToPackageToUpload:{_pathToPackageToUpload}");
                    }
                    return await message.Content.ReadAsStringAsync();
                }
            }
        }
    }

    public class AddPackageStrategy : IAzurelinuxRepositoryServiceHttpStrategy
    {
        private readonly IdInRepositoryService _idInRepositoryService;
        private readonly string _packageName;
        private readonly string _packageVersion;
        private readonly string _repositoryId;

        public AddPackageStrategy(
            IdInRepositoryService idInRepositoryService,
            string packageName,
            string packageVersion,
            string repositoryId)
        {
            _idInRepositoryService = idInRepositoryService
                                     ?? throw new ArgumentNullException(nameof(idInRepositoryService));
            _packageName = packageName;
            _packageVersion = packageVersion;
            _repositoryId = repositoryId;
        }

        public async Task<string> Execute(HttpClient client, Uri baseAddress)
        {
            var debianUploadJsonContent = new Dictionary<string, string>
            {
                ["name"] = _packageName,
                ["version"] = AppendDebianRevisionNumber(_packageVersion),
                ["fileId"] = _idInRepositoryService.Id,
                ["repositoryId"] = _repositoryId
            }.ToJson();
            var content = new StringContent(debianUploadJsonContent,
                Encoding.UTF8,
                "application/json");

            using (var response = await client.PostAsync(new Uri(baseAddress, "/v1/packages"), content))
            {
                if (!response.IsSuccessStatusCode)
                    throw new FailedToAddPackageToPackageRepositoryException(
                        $"request:{debianUploadJsonContent} response:{response.ToJson()}");
                return response.Headers.GetValues("Location").Single();
            }
        }

        private static string AppendDebianRevisionNumber(string packageVersion)
        {
            return packageVersion + "-1";
        }
    }

    public class PullQueuedPackageStatus : IAzurelinuxRepositoryServiceHttpStrategy
    {
        private readonly QueueResourceLocation _queueResourceLocation;

        public PullQueuedPackageStatus(QueueResourceLocation queueResourceLocation)
        {
            _queueResourceLocation = queueResourceLocation
                                     ?? throw new ArgumentNullException(nameof(queueResourceLocation));
        }

        public async Task<string> Execute(HttpClient client, Uri baseAddress)
        {
            using (var response = await client.GetAsync(new Uri(baseAddress, _queueResourceLocation.Location)))
            {
                if (!response.IsSuccessStatusCode)
                    throw new FailedToAddPackageToPackageRepositoryException(
                        "Failed to make request to " + _queueResourceLocation.Location);
                var body = await response.Content.ReadAsStringAsync();
                return !body.Contains("status") ? "" : JObject.Parse(body)["status"].ToString();
            }
        }
    }

    public class FailedToAddPackageToPackageRepositoryException : Exception
    {
        public FailedToAddPackageToPackageRepositoryException(string message) : base(message)
        {
        }

        public FailedToAddPackageToPackageRepositoryException()
        {
        }

        public FailedToAddPackageToPackageRepositoryException(string message, Exception innerException) : base(message,
            innerException)
        {
        }
    }

    public class RetryFailedException : Exception
    {
        public RetryFailedException(string message) : base(message)
        {
        }

        public RetryFailedException()
        {
        }

        public RetryFailedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public interface IAzurelinuxRepositoryServiceHttpStrategy
    {
        Task<string> Execute(HttpClient client, Uri baseAddress);
    }

    public class IdInRepositoryService
    {
        public IdInRepositoryService(string id)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
        }

        public string Id { get; }
    }

    public class QueueResourceLocation
    {
        public QueueResourceLocation(string location)
        {
            Location = location ?? throw new ArgumentNullException(nameof(location));
        }

        public string Location { get; }
    }
}