// Type-shape stubs only. No native CAD API or drawing/transaction validation.
namespace Autodesk.AutoCAD.DatabaseServices
{
    public class Database { public string Filename => "test.dwg"; }
    public class Transaction { }
    public struct Handle { public long Value => 10; }
    public struct ObjectId
    {
        public bool IsNull => false;
        public bool IsErased => false;
        public Handle Handle => new();
    }
    public class DBObject
    {
        public Handle Handle => new();
        public bool IsErased => false;
    }
    public class Entity : DBObject { public string Layer => "test-layer"; }
}
namespace Autodesk.AutoCAD.ApplicationServices
{
    public class Document { public string Name => "test.dwg"; }
    public class DocumentCollection { public Document MdiActiveDocument => new(); }
    public static class Application { public static DocumentCollection DocumentManager => new(); }
}
namespace Autodesk.AutoCAD.Geometry
{
    public record struct Point2d(double X, double Y);
    public record struct Point3d(double X, double Y, double Z);
    public record struct Vector2d(double X, double Y);
    public record struct Vector3d(double X, double Y, double Z);
}
