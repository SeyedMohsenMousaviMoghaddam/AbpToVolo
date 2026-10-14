using AbpToVolo.Steps;

namespace AbpToVolo.Tests;

/// <summary>Source endpoints (Boilerplate conventions) and their comparison with the converted host's swagger.json.</summary>
public sealed class EndpointParityTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "abptovolo-parity", Guid.NewGuid().ToString("N"));

    public EndpointParityTests()
    {
        Directory.CreateDirectory(Path.Combine(_source, "src/MyApp.Application/Customers"));
        Directory.CreateDirectory(Path.Combine(_source, "src/MyApp.Web.Core/Controllers"));
        File.WriteAllText(Path.Combine(_source, "src/MyApp.Application/Customers/CustomerAppService.cs"), """
            public class CustomerAppService : MyAppAppServiceBase, ICustomerAppService
            {
                public async Task<GetActiveCustomersOutput> GetActiveCustomersNameValue(GetActiveCustomersInput input) => null;
                public async Task<CustomerDto> GetCustomerAsync(long id) => null;
                public async Task UpsertCustomerAsync(UpsertCustomerInput input) { }
                public Task DeleteCustomer(long id) => Task.CompletedTask;
                [HttpGet] public Task<int> CountAsync() => null;
                [RemoteService(false)] public Task Hidden() => null;
                protected Task Helper() => null;
                public static void Utility() { }
            }
            """);
        File.WriteAllText(Path.Combine(_source, "src/MyApp.Web.Core/Controllers/TokenAuthController.cs"), """
            [Route("api/[controller]/[action]")]
            public class TokenAuthController : MyAppControllerBase
            {
                [HttpPost] public Task<AuthenticateResultModel> Authenticate([FromBody] AuthenticateModel model) => null;
                [HttpGet("{id}")] public Task<string> Captcha(string id) => null;
                [HttpGet, Route("/GetExcelReport")] public Task<FileResult> Report() => null;
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_source, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Source_endpoints_follow_the_boilerplate_conventions()
    {
        var endpoints = EndpointParity.SourceEndpoints(_source).Select(e => $"{e.Verb} {e.Path}").ToList();

        Assert.Contains("GET /api/services/app/Customer/GetActiveCustomersNameValue", endpoints);
        Assert.Contains("GET /api/services/app/Customer/GetCustomer", endpoints); // "Async" removed
        Assert.Contains("POST /api/services/app/Customer/UpsertCustomer", endpoints);
        Assert.Contains("DELETE /api/services/app/Customer/DeleteCustomer", endpoints);
        Assert.Contains("GET /api/services/app/Customer/Count", endpoints);
        Assert.Contains("POST /api/TokenAuth/Authenticate", endpoints);
        Assert.Contains("GET /api/TokenAuth/Captcha/{id}", endpoints);
        Assert.Contains("GET /GetExcelReport", endpoints);
        Assert.DoesNotContain(endpoints, e => e.Contains("Hidden") || e.Contains("Helper") || e.Contains("Utility"));
    }

    [Fact]
    public void Missing_endpoints_and_changed_verbs_are_found()
    {
        const string swagger = """
            { "openapi": "3.0.1", "paths": {
                "/api/services/app/Customer/GetActiveCustomersNameValue": { "get": {} },
                "/api/services/app/Customer/GetCustomer": { "get": {} },
                "/api/services/app/Customer/UpsertCustomer": { "post": {} },
                "/api/services/app/Customer/DeleteCustomer": { "post": {} },
                "/api/services/app/Customer/Count": { "get": {} },
                "/api/TokenAuth/Authenticate": { "post": {} },
                "/api/TokenAuth/Captcha/{captchaId}": { "get": {} }
            } }
            """;

        var result = EndpointParity.Compare(EndpointParity.SourceEndpoints(_source), EndpointParity.SwaggerEndpoints(swagger));

        Assert.Equal(8, result.Expected);
        Assert.Equal("/GetExcelReport", Assert.Single(result.Missing).Path);
        var changed = Assert.Single(result.VerbChanged);
        Assert.Equal("/api/services/app/Customer/DeleteCustomer", changed.Source.Path);
        Assert.Equal("POST", changed.ActualVerb);
    }
}
