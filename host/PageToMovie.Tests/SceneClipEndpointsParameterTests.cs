using System.Reflection;
using PageToMovie.Api;
using Xunit;

namespace PageToMovie.Tests;

/// <summary>
/// S107: delete handlers used to take 9–10 parameters. Route + query stay on the method;
/// services bind via <c>[AsParameters]</c> records so the count stays at or under 7.
/// </summary>
public sealed class SceneClipEndpointsParameterTests
{
    private const int SonarParameterLimit = 7;

    [Theory]
    [InlineData("DeleteProjectsIdScenesSceneClipsClip")]
    [InlineData("DeleteProjectsIdScenesScene")]
    public void Delete_handlers_stay_under_sonar_parameter_limit(string methodName)
    {
        var method = typeof(SceneClipEndpoints).GetMethod(
            methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True(
            method!.GetParameters().Length <= SonarParameterLimit,
            $"{methodName} has {method.GetParameters().Length} parameters (Sonar S107 allows {SonarParameterLimit}).");
    }
}
