using MongoDB.Bson;

namespace Zr3i.Data;

/// <summary>Ids generated in the application, so related rows can reference each other before any of them is written.</summary>
public static class NewId
{
    public static string Create() => ObjectId.GenerateNewId().ToString();
}
