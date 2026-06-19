using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DCAAIExtension
{
    internal class OpenAIClientAPI : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        /// <summary>
        /// Initializes the LlamaCPP OpenAI-compatible client.
        /// </summary>
        /// <param name="baseUrl">The base URL (e.g., "https://localhost:8080")</param>
        /// <param name="bearerToken">The secure bearer token</param>
        /// <param name="timeoutSeconds">HTTP Timeout in seconds</param>
        /// <param name="logger">Optional delegate to pipe logs to the VS Output Window</param>
        public OpenAIClientAPI(string baseUrl, string bearerToken, int timeoutSeconds = 120, Action<string> logger = null)
        {
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

            var handler = new HttpClientHandler
            {
                // Bypass SSL validation for remote/local self-signed certs
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    Log($"[Security Warning] Bypassing SSL certificate validation for {cert?.Subject}");
                    return true;
                }
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl.TrimEnd('/')),
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            Log($"Initialized OpenAIClientAPI connected to {_httpClient.BaseAddress}");
        }

        /// <summary>
        /// Sends a standard, blocking chat completion request.
        /// </summary>
        public async Task<ChatCompletionResponse> SendChatCompletionAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            request.Stream = false; // Ensure streaming is off for this method
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                Log("Sending chat completion request...");
                using var response = await _httpClient.PostAsync("/v1/chat/completions", content, cancellationToken);

                await EnsureSuccessAsync(response, cancellationToken);

                var responseJson = await response.Content.ReadAsStringAsync();
                Log("Chat completion received successfully.");

                return JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson);
            }
            catch (OperationCanceledException)
            {
                Log("Request was cancelled by the user or timed out.");
                throw;
            }
            catch (Exception ex)
            {
                Log($"Error in SendChatCompletionAsync: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sends a streaming chat completion request, yielding text chunks as they arrive.
        /// Ideal for streaming text directly into the Visual Studio editor.
        /// </summary>
        public async IAsyncEnumerable<string> StreamChatCompletionAsync(ChatCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            request.Stream = true; // Ensure streaming is on
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            Log("Starting chat completion stream...");

            // Use ResponseHeadersRead so we don't wait for the entire body
            using var response = await _httpClient.PostAsync("/v1/chat/completions", content, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            foreach (var MyLine in request.Messages)
            {
                Debug.WriteLine($"{MyLine.Role}: {MyLine.Content}");
            }

            Debug.WriteLine($"Response:");
            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data: [DONE]")) break;
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6); // remove "data: "
                    ChatCompletionChunkResponse chunk = null;
                    try
                    {
                        chunk = JsonSerializer.Deserialize<ChatCompletionChunkResponse>(data);
                    }
                    catch (JsonException ex)
                    {
                        Log($"Failed to parse chunk: {ex.Message}");
                        continue;
                    }

                    if (chunk?.Choices != null && chunk.Choices.Length > 0)
                    {
                        var textDelta = chunk.Choices[0].Delta?.Content;
                        if (!string.IsNullOrEmpty(textDelta))
                        {
                            Debug.Write($"{textDelta}");
                            yield return textDelta;
                        }
                    }
                }
            }

            Log("Stream completed.");
        }

        private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var errorMessage = $"API Error {(int)response.StatusCode} ({response.ReasonPhrase}): {errorContent}";
                Log(errorMessage);
                throw new HttpRequestException(errorMessage, null, response.StatusCode);
            }
        }

        private void Log(string message)
        {
            _logger?.Invoke($"[{DateTime.Now:HH:mm:ss}] OpenAIClientAPI: {message}");
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            Log("OpenAIClientAPI disposed.");
        }

        // --- Data Models ---

        public class ChatCompletionRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "llama";

            [JsonPropertyName("messages")]
            public ChatMessage[] Messages { get; set; }

            [JsonPropertyName("temperature")]
            public double? Temperature { get; set; }

            [JsonPropertyName("max_tokens")]
            public int? MaxTokens { get; set; }

            [JsonPropertyName("stream")]
            public bool? Stream { get; set; }
        }

        public class ChatMessage
        {
            [JsonPropertyName("role")]
            public string Role { get; set; }

            [JsonPropertyName("content")]
            public string Content { get; set; }
        }

        public class ChatCompletionResponse
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("choices")]
            public Choice[] Choices { get; set; }
        }

        public class Choice
        {
            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("message")]
            public ChatMessage Message { get; set; }

            [JsonPropertyName("finish_reason")]
            public string FinishReason { get; set; }
        }

        // Models for Streaming
        public class ChatCompletionChunkResponse
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }

            [JsonPropertyName("choices")]
            public ChunkChoice[] Choices { get; set; }
        }

        public class ChunkChoice
        {
            [JsonPropertyName("index")]
            public int Index { get; set; }

            [JsonPropertyName("delta")]
            public ChatMessage Delta { get; set; }

            [JsonPropertyName("finish_reason")]
            public string FinishReason { get; set; }
        }
    }
}