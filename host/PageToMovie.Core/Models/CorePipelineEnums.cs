namespace PageToMovie.Core.Models;

public enum UserRole
{
    Owner,
    Admin,
    Editor,
    Viewer
}

public enum AnalyticsWindow
{
    Hour,
    Day,
    Week,
    Month,
    All
}

public enum HttpHeader
{
    Authorization,
    ContentType,
    XApiKey
}

public enum ContainerType
{
    Mp4,
    WebM,
    Mov,
    Mkv
}

public enum Stage1JobType
{
    Parse,
    Convert,
    Sign
}
