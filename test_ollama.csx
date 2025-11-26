#r "nuget: System.Net.Http.Json, 9.0.0"
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

var client = new HttpClient { BaseAddress = new Uri("http://localhost:11434") };

var request = new { model = "nomic-embed-text", prompt = "test embedding" };
var response = await client.PostAsJsonAsync("/api/embeddings", request);
Console.WriteLine($"Status: {response.StatusCode}");

var result = await response.Content.ReadAsStringAsync();
Console.WriteLine($"Response: {result.Substring(0, Math.Min(200, result.Length))}...");
