﻿using IdentityModel.Client;
using IdentityServer4.Models;
using IdentityServer4.Stores;
using IdentityServer4.Validation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SInnovations.Azure.TableStorageRepository;
using SInnovations.Azure.TableStorageRepository.Queryable;
using SInnovations.Azure.TableStorageRepository.TableRepositories;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNETDevOps.Identity.AzureB2CUserService
{
    public class PersistedGrantContext : TableStorageContext
    {
        public PersistedGrantContext(
            ILoggerFactory loggerFactory,
            IEntityTypeConfigurationsContainer container,
            CloudStorageAccount account) :
            base(loggerFactory, container, account)
        {
            this.InsertionMode = InsertionMode.AddOrReplace;
        }

        protected override void OnModelCreating(TableStorageModelBuilder modelbuilder)
        {

            modelbuilder.Entity<PersistedGrant>()
               .HasKeys(k => new { k.SubjectId, k.ClientId }, r => new { r.Type, r.Key })
               .WithIndex(k => k.Key, true, "idsrvstore")
               .WithKeyTransformation(k => k.Key, FixPartitionKey)
               .ToTable("idsrvstore");

            base.OnModelCreating(modelbuilder);
        }

        public static string FixPartitionKey(string p)
        {
            return $"idx__{p.Replace('/', '_')}";
        }

        public ITableRepository<PersistedGrant> PersistedGrants { get; set; }
    }
    public class TableStoragePersistedGrantStore : IPersistedGrantStore
    {
        private readonly PersistedGrantContext _context;
        public TableStoragePersistedGrantStore(PersistedGrantContext context)
        {
            this._context = context;
        }
        public async Task<IEnumerable<PersistedGrant>> GetAllAsync(string subjectId)
        {
            return await _context.PersistedGrants.Where(k => k.SubjectId == subjectId).ToListAsync().ConfigureAwait(false);
        }

        public async Task<PersistedGrant> GetAsync(string key)
        {
            return await _context.PersistedGrants.FindByIndexAsync(key).ConfigureAwait(false);
        }
        public async Task StoreAsync(PersistedGrant grant)
        {

            this._context.PersistedGrants.Add(grant);

            await this._context.SaveChangesAsync().ConfigureAwait(false);
        }



        public Task RemoveAllAsync(string subjectId, string clientId)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAllAsync(string subjectId, string clientId, string type)
        {
            return Task.CompletedTask;
        }

        public async Task RemoveAsync(string key)
        {

            this._context.PersistedGrants.Delete(await _context.PersistedGrants.FindByIndexAsync(key).ConfigureAwait(false));
            await Task.WhenAll(
                this._context.PersistedGrants.DeleteByKey(PersistedGrantContext.FixPartitionKey(key), ""),
                this._context.SaveChangesAsync());

        }




    }

    internal struct ExpirationMetadata<T>
    {
        public T Result { get; set; }

        public DateTimeOffset ValidUntil { get; set; }
    }

    internal class AsyncExpiringLazy<T>
    {
        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(initialCount: 1);
        private readonly Func<ExpirationMetadata<T>, Task<ExpirationMetadata<T>>> _valueProvider;
        private ExpirationMetadata<T> _value;

        public AsyncExpiringLazy(Func<ExpirationMetadata<T>, Task<ExpirationMetadata<T>>> valueProvider)
        {
            if (valueProvider == null) throw new ArgumentNullException(nameof(valueProvider));
            _valueProvider = valueProvider;
        }

        private bool IsValueCreatedInternal => _value.Result != null && _value.ValidUntil > DateTimeOffset.UtcNow;

        public async Task<bool> IsValueCreated()
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return IsValueCreatedInternal;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        public async Task<T> Value()
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsValueCreatedInternal)
                {
                    return _value.Result;
                }

                var result = await _valueProvider(_value).ConfigureAwait(false);
                _value = result;
                return _value.Result;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        public async Task Invalidate()
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            _value = default(ExpirationMetadata<T>);
            _syncLock.Release();
        }
    }

    public class AzureB2CUserServiceAutenticationService : IAzureB2CUserServiceAutenticationService
    {
        public static string aadGraphResourceId = "https://graph.windows.net/";

        private readonly AzureB2CUserServiceConfiguration _configuration;
        private readonly AuthenticationContext _authenticationContext;
        private readonly AsyncExpiringLazy<string> _token;

        public AzureB2CUserServiceAutenticationService(IOptions<AzureB2CUserServiceConfiguration> options)
        {
            _configuration = options.Value ?? throw new ArgumentNullException(nameof(options));

            _authenticationContext = new AuthenticationContext("https://login.microsoftonline.com/" + _configuration.TenantId);

            _token = new AsyncExpiringLazy<string>(async (old) =>
              {
                  AuthenticationResult result = await _authenticationContext.AcquireTokenAsync(aadGraphResourceId, new ClientCredential(_configuration.ApplicationId, _configuration.ClientSecret));

                  return new ExpirationMetadata<string> { ValidUntil = result.ExpiresOn.AddMinutes(5), Result = result.AccessToken };
              });
        }

        public virtual async Task<string> GetClientSecretAsync()
        {
            return _configuration.ClientSecret;
        }

        public virtual async Task AuthenticateAsync(HttpRequestMessage request)
        {
            
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",await _token.Value());
           
        }
    }
    public class AzureB2CUserService<T> : IResourceOwnerPasswordValidator where T : AzureB2CUser
    {
        public static string aadInstance = "https://login.microsoftonline.com/";
        public static string aadGraphResourceId = "https://graph.windows.net/";
        public static string aadGraphEndpoint = "https://graph.windows.net/";
        public static string aadGraphSuffix = "";
        public static string aadGraphVersion = "api-version=1.6";

        private readonly AzureB2CUserServiceConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAzureB2CUserServiceAutenticationService _azureB2CUserServiceAutenticationService;
        private readonly ILogger<AzureB2CUserService<T>> _logger;

        // private string tenant { get; set; } = "dotnetdevopsb2c.onmicrosoft.com";

        //  public string appId = "c21f025e-159f-4aa8-821d-18d7f145e2f9";

        // private readonly Task<string> AppKey;
     //   private readonly IHostingEnvironment _hostingEnvironment;


        // 


        public async Task ValidateAsync(ResourceOwnerPasswordValidationContext context)
        {
            var a = new TokenClient($"https://login.microsoftonline.com/{_configuration.TenantId}/oauth2/token", 
                _configuration.ApplicationId, 
                await _azureB2CUserServiceAutenticationService.GetClientSecretAsync(), style: AuthenticationStyle.PostValues);
             
            var optional = new
            {
                resource = aadGraphResourceId,
            };

            var b = await a.RequestResourceOwnerPasswordAsync(context.UserName.Replace("@", "_"), context.Password, extra: optional); ;

            if (!string.IsNullOrEmpty(b.AccessToken))
            { 
                var jsonToken = new JwtSecurityTokenHandler().ReadToken(b.AccessToken) as JwtSecurityToken;

                var claims = new List<Claim>
                {
                    new Claim("sub", jsonToken.Claims.FirstOrDefault(c => c.Type == "oid").Value)
                };
                context.Result = new GrantValidationResult
                {
                    Subject = new ClaimsPrincipal(new ClaimsIdentity(claims))
                };
            }
            else
            {

                context.Result = new GrantValidationResult(TokenRequestErrors.InvalidGrant, b.ErrorDescription);

            }

        }





      //  private AuthenticationContext authContext;
      //  private Task<ClientCredential> credential;

        public AzureB2CUserService(
            IOptions<AzureB2CUserServiceConfiguration> options, 
            IHttpClientFactory httpClientFactory,
            IAzureB2CUserServiceAutenticationService azureB2CUserServiceAutenticationService, 
            ILogger<AzureB2CUserService<T>> logger)
        {

            _configuration = options.Value ?? throw new ArgumentNullException(nameof(options));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));           
            _azureB2CUserServiceAutenticationService = azureB2CUserServiceAutenticationService ?? throw new ArgumentNullException(nameof(azureB2CUserServiceAutenticationService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
          

        }
        public async Task<ODataResult<string[]>> GetUserRolesAsync(string objectId)
        {
            var result = await SendGraphPostRequest($"/users/{objectId}/getMemberGroups", $"{{\"securityEnabledOnly\": true}}");

            return new ODataResult<string[]> { Value = result.Object.SelectToken("$.value").ToObject<string[]>() };
        }

        public async Task<AzureB2CResult> AddToGroupAsync(string groupId,string userId)
        {
            var result = await SendGraphPostRequest($"/groups/{groupId}/$links/members",JsonConvert.SerializeObject(new { url = $"{aadGraphEndpoint}{_configuration.TenantId}/directoryObjects/{userId}" }));

            return result;
        }

        public async Task<AzureB2CResult> RemoveFromGroupAsync(string groupId, string userId)
        {
            var result = await SendGraphDeleteRequest($"/groups/{groupId}/$links/members/{userId}");

            return result;
        }


        public async Task<ODataResult<T>> GetUserByObjectIdAsync(string objectId)
        {
            var result = await SendGraphGetRequest("/users/" + objectId, null);
            return new ODataResult<T> { Value = result.Object.ToObject<T>() };
        }
        public async Task<ODataResult<T>> GetUserBySigninName(string username)
        {
            var result = await SendGraphGetRequest("/users", $"$filter=signInNames/any(x:x/value eq '{username}')");

            if (result.IsError)
                return new ODataResult<T> { };

            var oresult = result.Object.ToObject<ODataResult<T[]>>();

            return new ODataResult<T> { Value = oresult.Value.FirstOrDefault() };
        }

        public async Task<ODataResult<T[]>> GetAllUsersAsync(string query = null)
        {
            var result = await SendGraphGetRequest("/users", query);

            return result.Object.ToObject<ODataResult<T[]>>();
        }

        public Task<AzureB2CResult> CreateUserAsync(T user)
        {
            var a = new List<SignInNames>(user.signInNames);
            var mails = new List<string>();
            foreach (var signInName in user.signInNames.ToArray().Where(s => s.type == "emailAddress"))
            {
                a.Add(new SignInNames
                {
                    type = "userName",
                    value = signInName.value.Replace("@", "_")
                });
                //user.o = user.mailNickName ?? signInName.value;
                mails.Add(signInName.value);
            }
            user.otherMails = mails.ToArray();
            user.signInNames = a.ToArray();
            //user.Environment = _hostingEnvironment.EnvironmentName;

            return SendGraphPostRequest("/users", JsonConvert.SerializeObject(user,new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }
        public async Task<AzureB2CResult> SetPasswordAsync(string objectId, string newPassword)
        {
            return await SendGraphPatchRequest($"/users/{objectId}", JsonConvert.SerializeObject(new { passwordProfile = new PasswordProfile { forceChangePasswordNextLogin = false, password = newPassword } }));
        }

        private JsonSerializerSettings settings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore };
        public Task<AzureB2CResult> UpdateUser(string objectId, PatchAzureB2CUser user)
        {
            if (user.signInNames != null)
            {
                var a = new List<SignInNames>(user.signInNames);
                var mails = new List<string>();
                foreach (var signInName in user.signInNames.ToArray().Where(s => s.type == "emailAddress"))
                {
                    a.Add(new SignInNames
                    {
                        type = "userName",
                        value = signInName.value.Replace("@", "_")
                    });
                    //user.o = user.mailNickName ?? signInName.value;
                    mails.Add(signInName.value);
                }
                user.otherMails = mails.ToArray();
                user.signInNames = a.ToArray();
            }

            return SendGraphPatchRequest("/users/" + objectId, JsonConvert.SerializeObject(user, settings: settings));
        }

        public async Task<AzureB2CResult> DeleteUser(string objectId)
        {
            return await SendGraphDeleteRequest("/users/" + objectId);
        }

        public Task<AzureB2CResult> RegisterExtension(string objectId, string body)
        {
            return SendGraphPostRequest("/applications/" + objectId + "/extensionProperties", body);
        }

        public async Task<AzureB2CResult> UnregisterExtension(string appObjectId, string extensionObjectId)
        {
            return await SendGraphDeleteRequest("/applications/" + appObjectId + "/extensionProperties/" + extensionObjectId);
        }

        public async Task<AzureB2CResult> GetExtensions(string appObjectId)
        {
            return await SendGraphGetRequest("/applications/" + appObjectId + "/extensionProperties", null);
        }

        public async Task<AzureB2CResult> GetApplications(string query)
        {
            return await SendGraphGetRequest("/applications", query);
        }

        private async Task<AzureB2CResult> SendGraphDeleteRequest(string api)
        {
            // NOTE: This client uses ADAL v2, not ADAL v4
          //  AuthenticationResult result = await authContext.AcquireTokenAsync(aadGraphResourceId, await credential);
            HttpClient http = _httpClientFactory.CreateClient();
            string url = aadGraphEndpoint + _configuration.TenantId + api + "?" + aadGraphVersion;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, url);
          //  request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
            await _azureB2CUserServiceAutenticationService.AuthenticateAsync(request);
            HttpResponseMessage response = await http.SendAsync(request);

            _logger.LogInformation("Sending Delete request: {url}",url);


            if (!response.IsSuccessStatusCode)
            {
                return new AzureB2CResult { Error = JObject.Parse(await response.Content.ReadAsStringAsync())["odata.error"].ToObject<OdataError>() };
            }

            return new AzureB2CResult();
 
        }

        private async Task<AzureB2CResult> SendGraphPatchRequest(string api, string json)
        {
            
            HttpClient http = _httpClientFactory.CreateClient();
            string url = aadGraphEndpoint + _configuration.TenantId + api + "?" + aadGraphVersion;

            _logger.LogInformation("Sending PATCH request: {url}\nContent-Type: application/json\n{json}\n", url,json);
              
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("PATCH"), url);
           
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            await _azureB2CUserServiceAutenticationService.AuthenticateAsync(request);

            HttpResponseMessage response = await http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return new AzureB2CResult { Error = JObject.Parse(await response.Content.ReadAsStringAsync())["odata.error"].ToObject<OdataError>() };
            }

            return new AzureB2CResult();
        }

        private async Task<AzureB2CResult> SendGraphPostRequest(string api, string json)
        {
            HttpClient http = _httpClientFactory.CreateClient();
            string url = aadGraphEndpoint + _configuration.TenantId + api + "?" + aadGraphVersion;

            _logger.LogInformation("Sending POST request: {url}\nContent-Type: application/json\n{json}\n", url, json);

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
            
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            await _azureB2CUserServiceAutenticationService.AuthenticateAsync(request);

            HttpResponseMessage response = await http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                //   string error = await response.Content.ReadAsStringAsync();
                // object formatted = JsonConvert.DeserializeObject(error);
                var responseTxt = await response.Content.ReadAsStringAsync();
                try
                {
                    return new AzureB2CResult { Error = JObject.Parse(responseTxt)["odata.error"].ToObject<OdataError>() };
                }
                catch (Exception ex)
                {
                    return new AzureB2CResult { Error = new OdataError { message = new ODataErrorMessage { value = responseTxt } } };
                }

                //throw new WebException("Error Calling the Graph API: \n" + JsonConvert.SerializeObject(formatted, Formatting.Indented));
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine((int)response.StatusCode + ": " + response.ReasonPhrase);
            Console.WriteLine("");
            if (response.StatusCode == HttpStatusCode.NoContent)
                return new AzureB2CResult { };

            return new AzureB2CResult { Object = JObject.Parse(await response.Content.ReadAsStringAsync()) };

        }

        public async Task<AzureB2CResult> SendGraphGetRequest(string api, string query)
        {

            // For B2C user managment, be sure to use the beta Graph API version.
            HttpClient http = _httpClientFactory.CreateClient();
            string url = "https://graph.windows.net/" + _configuration.TenantId + api + "?" + aadGraphVersion;
            if (!string.IsNullOrEmpty(query))
            {
                url += "&" + query.TrimStart('?');
            }

            _logger.LogInformation("Sending GET request: {url}", url);

            // Append the access token for the Graph API to the Authorization header of the request, using the Bearer scheme.
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            await _azureB2CUserServiceAutenticationService.AuthenticateAsync(request);
            HttpResponseMessage response = await http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                return new AzureB2CResult { Error = JObject.Parse(await response.Content.ReadAsStringAsync()).SelectToken("$.odata.error").ToObject<OdataError>() };

                // string error = await response.Content.ReadAsStringAsync();
                //object formatted = JsonConvert.DeserializeObject(error);
                //throw new WebException("Error Calling the Graph API: \n" + JsonConvert.SerializeObject(formatted, Formatting.Indented));
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine((int)response.StatusCode + ": " + response.ReasonPhrase);
            Console.WriteLine("");

            return new AzureB2CResult { Object = JObject.Parse(await response.Content.ReadAsStringAsync()) };

            // return await response.Content.ReadAsStringAsync();
        }


    }

    public class ODataErrorMessage
    {
        public string lang { get; set; }
        public string value { get; set; }
    }
    public class OdataErrorValue
    {
        public string item { get; set; }
        public string value { get; set; }
    }
    public class OdataError
    {
        public string code { get; set; }
        public DateTime date { get; set; }
        public ODataErrorMessage message { get; set; }
        public string requestId { get; set; }
        public List<OdataErrorValue> values { get; set; }
    }

}
