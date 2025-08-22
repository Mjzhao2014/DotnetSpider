using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetSpider.Tests.Throttling;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<Task<HttpResponseMessage>>> _responses = new();
    private Func<Task<HttpResponseMessage>> _defaultResponse;

    public void Setup(HttpStatusCode statusCode, string content)
    {
        _defaultResponse = () => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new System.Net.Http.StringContent(content)
        });
    }

    public void SetupWithDelay(HttpStatusCode statusCode, string content, TimeSpan delay)
    {
        _defaultResponse = async () =>
        {
            await Task.Delay(delay);
            return new HttpResponseMessage(statusCode)
            {
                Content = new System.Net.Http.StringContent(content)
            };
        };
    }

    public MockHttpMessageHandlerSetup SetupSequence()
    {
        return new MockHttpMessageHandlerSetup(this);
    }

    internal void AddResponse(Func<Task<HttpResponseMessage>> responseFactory)
    {
        _responses.Enqueue(responseFactory);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responses.Count > 0)
        {
            var responseFactory = _responses.Dequeue();
            return await responseFactory();
        }

        if (_defaultResponse != null)
        {
            return await _defaultResponse();
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("Default Response")
        };
    }
}

public class MockHttpMessageHandlerSetup
{
    private readonly MockHttpMessageHandler _handler;

    public MockHttpMessageHandlerSetup(MockHttpMessageHandler handler)
    {
        _handler = handler;
    }

    public MockHttpMessageHandlerSetup Returns(HttpResponseMessage response)
    {
        _handler.AddResponse(() => Task.FromResult(response));
        return this;
    }

    public MockHttpMessageHandlerSetup Returns(Func<Task<HttpResponseMessage>> responseFactory)
    {
        _handler.AddResponse(responseFactory);
        return this;
    }
}