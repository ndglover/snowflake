/*
* Copyright (c) 2025 ADBC Drivers Contributors
*
* This file has been modified from its original version, which is
* under the Apache License:
*
* Licensed to the Apache Software Foundation (ASF) under one
* or more contributor license agreements.  See the NOTICE file
* distributed with this work for additional information
* regarding copyright ownership.  The ASF licenses this file
* to you under the Apache License, Version 2.0 (the
* "License"); you may not use this file except in compliance
* with the License.  You may obtain a copy of the License at
*
*    http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdbcDrivers.Snowflake.Native.Services.Authentication;

using Apache.Arrow;
using Apache.Arrow.Adbc;

namespace AdbcDrivers.Snowflake.Native.Services.Transport;

/// <summary>
/// Implements HTTP communication with Snowflake's REST API.
/// </summary>
internal class RestApiClient : IRestApiClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _enableCompression;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseRetryDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="enableCompression">Whether to enable compression.</param>
    /// <param name="maxRetries">Maximum number of retries for transient errors.</param>
    /// <param name="baseRetryDelay">Base delay for exponential backoff.</param>
    public RestApiClient(
        HttpClient httpClient,
        bool enableCompression = true,
        int maxRetries = 3,
        TimeSpan? baseRetryDelay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _enableCompression = enableCompression;
        _maxRetries = maxRetries;
        _baseRetryDelay = baseRetryDelay ?? TimeSpan.FromMilliseconds(100);
    }

    /// <inheritdoc/>
    public async Task<ApiResponse<T>> PostAsync<T>(
        string endpoint,
        object request,
        AuthenticationToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint, nameof(endpoint));
        ArgumentNullException.ThrowIfNull(token, nameof(token));

        return await ExecuteWithRetryAsync(async () =>
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
            ConfigureRequest(requestMessage, token);

            requestMessage.Content = JsonContent.Create(request);
            AddCompressionHeadersIfEnabled(requestMessage);

            var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await ReadApiResponseAsync<T>(response, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Stream> GetArrowStreamAsync(
        string url,
        AuthenticationToken token,
        Dictionary<string, string>? chunkHeaders = null,
        string? qrmk = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url, nameof(url));
        ArgumentNullException.ThrowIfNull(token, nameof(token));

        return await ExecuteWithRetryAsync(async () =>
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            if (chunkHeaders == null && string.IsNullOrEmpty(qrmk))
            {
                ConfigureRequest(requestMessage, token);
            }
            else
            {
                if (chunkHeaders != null)
                {
                    foreach (var header in chunkHeaders)
                    {
                        requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                else
                {
                    requestMessage.Headers.TryAddWithoutValidation("x-amz-server-side-encryption-customer-algorithm", "AES256");
                    requestMessage.Headers.TryAddWithoutValidation("x-amz-server-side-encryption-customer-key", qrmk);
                }
            }

            requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.apache.arrow.stream"));

            var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await GetResponseStreamAsync(response, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ApiResponse<T>> GetAsync<T>(
        string endpoint,
        AuthenticationToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint, nameof(endpoint));
        ArgumentNullException.ThrowIfNull(token, nameof(token));

        return await ExecuteWithRetryAsync(async () =>
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint);
            ConfigureRequest(requestMessage, token);

            var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            response.EnsureSuccessStatusCode();

            return await ReadApiResponseAsync<T>(response, cancellationToken);
        }, cancellationToken);
    }

    private void AddCompressionHeadersIfEnabled(HttpRequestMessage request)
    {
        if (!_enableCompression) return;

        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
    }

    private async Task<Stream> GetResponseStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (response.Content.Headers.ContentEncoding.Contains("gzip"))
            return new GZipStream(stream, CompressionMode.Decompress);

        if (response.Content.Headers.ContentEncoding.Contains("deflate"))
            return new DeflateStream(stream, CompressionMode.Decompress);

        return stream;
    }

    private async Task<ApiResponse<T>> ReadApiResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await GetResponseStreamAsync(response, cancellationToken);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<ApiResponse<T>>(json)
            ?? throw new InvalidOperationException("Failed to deserialize API response.");

        return result;
    }

    private void ConfigureRequest(HttpRequestMessage request, AuthenticationToken token)
    {
        var sessionToken = token.SessionToken ?? token.AccessToken;
        var authHeader = $"Snowflake Token=\"{sessionToken}\"";
        request.Headers.Add("Authorization", authHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/snowflake"));

        // Add user agent - match Snowflake connector format to enable Arrow support
        // Reference: snowflake-connector-net sends ".NET/{version}" as driver name
        request.Headers.UserAgent.ParseAdd(".NET/1.0.0");
        request.Headers.UserAgent.ParseAdd("(Windows)");
        request.Headers.UserAgent.ParseAdd(".NETCoreApp/8.0");
    }

    private async Task<TResult> ExecuteWithRetryAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < _maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (IsTransientError(ex) && attempt < _maxRetries - 1)
            {
                lastException = ex;
                await DelayAsync(attempt, cancellationToken);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && attempt < _maxRetries - 1)
            {
                lastException = ex;
                await DelayAsync(attempt, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("Operation failed after retries.");
    }

    async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(
            _baseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt) +
            Random.Shared.Next(0, 100));
        await Task.Delay(delay, cancellationToken);
    }

    private static bool IsTransientError(HttpRequestException ex)
    {
        // Check for transient HTTP status codes
        if (ex.StatusCode.HasValue)
        {
            var statusCode = (int)ex.StatusCode.Value;
            return statusCode == 408 || // Request Timeout
                   statusCode == 429 || // Too Many Requests
                   statusCode == 503 || // Service Unavailable
                   statusCode == 504;   // Gateway Timeout
        }

        // Check for network-related errors
        return ex.InnerException is System.Net.Sockets.SocketException ||
               ex.InnerException is IOException;
    }
}
