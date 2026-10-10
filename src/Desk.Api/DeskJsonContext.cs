using System.Text.Json;
using System.Text.Json.Serialization;
using Desk.Api.Auth;
using Desk.Api.Positions;
using Desk.Data.Funds;
using Desk.Data.Insights;
using Desk.Data.Grid;

namespace Desk.Api;

/// <summary>
/// System.Text.Json source generation for the API's DTOs (README §8): no reflection on the hot path, trimming-safe.
/// The positions block itself is written by <see cref="ColumnarSerializer"/> straight from its column buffers.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GridRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(MeResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(DbHealthResponse))]
[JsonSerializable(typeof(AsOfResponse))]
[JsonSerializable(typeof(CatalogColumn[]))]
[JsonSerializable(typeof(PortfolioResponse[]))]
[JsonSerializable(typeof(PresetResponse[]))]
[JsonSerializable(typeof(SavePresetRequest))]
[JsonSerializable(typeof(BuiltInState))]
[JsonSerializable(typeof(FundPerformance))]
[JsonSerializable(typeof(InsightsResult))]
// P3 grid cells are object: the runtime types they can hold must be known to the generator.
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(int))]
public sealed partial class DeskJsonContext : JsonSerializerContext;
