using System.Globalization;
using System.Text.Json;
using Playnite.Core.Tests.Models;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
Console.WriteLine(JsonSerializer.Serialize(ModelProbe.Observe(), new JsonSerializerOptions { WriteIndented = true }));
