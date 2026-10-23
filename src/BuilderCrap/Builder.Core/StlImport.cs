namespace Builder.Core;

/// <summary>Strict refuses nonzero binary STL attribute words rather than guessing a color dialect.
/// GeometryOnly explicitly imports triangles while discarding attribute/color data. Neither mode interprets colors.</summary>
public enum StlImportMode { Strict=0,GeometryOnly=1 }
