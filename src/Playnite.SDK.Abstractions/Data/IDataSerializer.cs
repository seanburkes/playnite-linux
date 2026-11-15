namespace Playnite.SDK.Data;

/// <summary>Describes the host data serializer. Implementations live outside the contracts assembly.</summary>
public interface IDataSerializer
{
    string ToYaml(object? obj);
    T? FromYaml<T>(string yaml) where T : class;
    bool TryFromYaml<T>(string yaml, out T? content) where T : class;
    bool TryFromYaml<T>(string yaml, out T? content, out Exception? error) where T : class;
    T? FromYamlFile<T>(string filePath) where T : class;
    bool TryFromYamlFile<T>(string filePath, out T? content) where T : class;
    bool TryFromYamlFile<T>(string filePath, out T? content, out Exception? error) where T : class;
    string ToJson(object? obj, bool formatted = false);
    void ToJsonStream(object? obj, Stream stream, bool formatted = false);
    T? FromJson<T>(string json) where T : class;
    bool TryFromJson<T>(string json, out T? content) where T : class;
    bool TryFromJson<T>(string json, out T? content, out Exception? error) where T : class;
    T? FromJsonStream<T>(Stream stream) where T : class;
    bool TryFromJsonStream<T>(Stream stream, out T? content) where T : class;
    bool TryFromJsonStream<T>(Stream stream, out T? content, out Exception? error) where T : class;
    T? FromJsonFile<T>(string filePath) where T : class;
    bool TryFromJsonFile<T>(string filePath, out T? content) where T : class;
    bool TryFromJsonFile<T>(string filePath, out T? content, out Exception? error) where T : class;
    T? FromToml<T>(string toml) where T : class;
    bool TryFromToml<T>(string toml, out T? content) where T : class;
    bool TryFromToml<T>(string toml, out T? content, out Exception? error) where T : class;
    T? FromTomlFile<T>(string filePath) where T : class;
    bool TryFromTomlFile<T>(string filePath, out T? content) where T : class;
    bool TryFromTomlFile<T>(string filePath, out T? content, out Exception? error) where T : class;
    T? GetClone<T>(T? source) where T : class;
    U? GetClone<T, U>(T? source) where T : class where U : class;
    bool AreObjectsEqual(object? object1, object? object2);
}
