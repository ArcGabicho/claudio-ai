namespace ClaudioAi.Projects;

public enum ProjectKind { Windows, Wsl }

/// <summary>Un proyecto real encontrado bajo una de las carpetas configuradas.</summary>
public sealed record ProjectRef(string Name, ProjectKind Kind, string Path);
