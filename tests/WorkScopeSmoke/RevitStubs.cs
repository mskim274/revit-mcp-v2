// Small test double for policy execution only. Production builds separately
// compile WorkScopeGuard against both real supported Revit API references.
namespace Autodesk.Revit.DB
{
    public enum BuiltInParameter { ALL_MODEL_INSTANCE_COMMENTS = -100, ALL_MODEL_MARK = -101 }
    public enum StorageType { String, Double, Integer }
    public class ElementId(long value)
    {
        public long Value = value;
        public static readonly ElementId InvalidElementId = new(-1);
    }
    public class Parameter(long id, StorageType storage = StorageType.String)
    {
        public ElementId Id = new(id);
        public StorageType StorageType = storage;
        public bool IsReadOnly;
    }
    public class Element
    {
        public ElementId TypeId = new(1000);
        public ElementId LevelId = new(2000);
        public Dictionary<string, List<Parameter>> Parameters = new();
        public ElementId GetTypeId() => TypeId;
        public IList<Parameter> GetParameters(string name) => Parameters.GetValueOrDefault(name) ?? new();
    }
    public class ElementType : Element { }
    public class Document
    {
        public Dictionary<long, Element> Elements = new();
        public Element GetElement(ElementId id) => Elements.GetValueOrDefault(id.Value);
    }
}
namespace RevitMCP.CommandSet.Interfaces
{
    public static class ElementIdCompatibility
    {
        public static Autodesk.Revit.DB.ElementId Create(long value) => new(value);
        public static long GetValue(this Autodesk.Revit.DB.ElementId id) => id.Value;
    }
}
