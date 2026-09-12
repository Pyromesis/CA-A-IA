// CA-A-IA — Tests web (parseo DDG, limpieza HTML, guardas SSRF). Sin red real.

using System.Net;
using System.Text;
using CaAIA.Domain.Enums;
using CaAIA.Domain.Tools;
using CaAIA.Infrastructure.Tools;

namespace CaAIA.Tests.Unit;

public sealed class WebToolsTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => Task.FromResult(_fn(request));
    }

    private static ToolInvocation Invoke(string toolId, string args) =>
        new(Guid.NewGuid(), toolId, args,
            new Domain.Correlation.CorrelationContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    [Fact]
    public void WebTools_RequireNetwork()
    {
        Assert.Equal(ToolPermission.Network, new WebSearchTool(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)))).Definition.RequiredPermissions);
        Assert.Equal(ToolPermission.Network, new WebFetchTool(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)))).Definition.RequiredPermissions);
    }

    [Fact]
    public void ParseDuckResults_UnwrapsRedirects()
    {
        const string html = """
            <a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa">Example A</a>
            <a class="result__snippet">First snippet here</a>
            <a class="result__a" href="https://example.com/b">Example B</a>
            """;
        var results = WebSearchTool.ParseDuckResults(html);
        Assert.Equal(2, results.Count);
        Assert.Equal("https://example.com/a", results[0].Url);
        Assert.Equal("Example A", results[0].Title);
        Assert.Equal("First snippet here", results[0].Snippet);
        Assert.Equal("https://example.com/b", results[1].Url);
        Assert.Empty(WebSearchTool.ParseDuckResults("<html>sin resultados</html>"));
    }

    [Fact]
    public void HtmlToText_StripsNoise()
    {
        const string html = """
            <html><head><title>T</title><script>var x=1;</script><style>.a{}</style></head>
            <body><nav>menu</nav><h1>Hola &amp; adiós</h1><p>Texto  con   espacios</p></body></html>
            """;
        var text = WebFetchTool.HtmlToText(html);
        Assert.Contains("Hola & adiós", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("menu", text);
        Assert.Equal("T", WebFetchTool.ExtractTitle(html));
    }

    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://localhost:8080/", false)]
    [InlineData("https://127.0.0.1/", false)]
    [InlineData("https://10.0.0.5/x", false)]
    [InlineData("https://192.168.1.1/", false)]
    [InlineData("https://169.254.169.254/", false)]
    [InlineData("file:///etc/passwd", false)]
    public async Task ValidateUrl_BlocksNonPublic(string url, bool allowed)
    {
        if (allowed)
        {
            Assert.Equal("https://example.com/a", await WebSecurity.ValidateUrlAsync(url, CancellationToken.None));
            return;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => WebSecurity.ValidateUrlAsync(url, CancellationToken.None));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    public void IsBlockedIp_Classifies(string ip, bool expected)
    {
        Assert.Equal(expected, WebSecurity.IsBlockedIp(System.Net.IPAddress.Parse(ip)));
    }

    [Fact]
    public async Task WebSearch_Flow_ParsesResults()
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """<a class="result__a" href="https://example.com/a">A</a>""",
                Encoding.UTF8, "text/html"),
        }));
        var result = await new WebSearchTool(http).ExecuteAsync(
            Invoke(WebSearchTool.ToolId, """{"query":"test"}"""), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("https://example.com/a", result.Output);
    }

    [Fact]
    public async Task WebFetch_Flow_ExtractsText()
    {
        var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><head><title>T</title></head><body><p>Hola mundo</p></body></html>",
                Encoding.UTF8, "text/html"),
        }));
        var result = await new WebFetchTool(http).ExecuteAsync(
            Invoke(WebFetchTool.ToolId, """{"url":"https://example.com/"}"""), CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Contains("Hola mundo", result.Output);
    }

    [Fact]
    public async Task WebFetch_BlockedHost_FailsClean()
    {
        var http = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("must not call network")));
        var result = await new WebFetchTool(http).ExecuteAsync(
            Invoke(WebFetchTool.ToolId, """{"url":"http://localhost:9/"}"""), CancellationToken.None);
        Assert.False(result.Success);
    }
}
