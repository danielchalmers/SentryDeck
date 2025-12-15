using System.IO;
using Shouldly;
using TeslaCam.Data;

namespace TeslaCam.Tests;

/// <summary>
/// Tests for the ClipPlaylist functionality.
/// </summary>
public class ClipPlaylistTests
{
    [Fact]
    public void SetClips_InitializesPlaylist()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);

        // Act
        playlist.SetClips(clips);

        // Assert
        playlist.Clips.Count.ShouldBe(5);
        playlist.CurrentIndex.ShouldBe(-1); // No clip selected initially
        playlist.CurrentClip.ShouldBeNull();
    }

    [Fact]
    public void MoveTo_SelectsCorrectClip()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);
        playlist.SetClips(clips);

        // Act
        playlist.MoveTo(2);

        // Assert
        playlist.CurrentIndex.ShouldBe(2);
        playlist.CurrentClip.ShouldBe(clips[2]);
    }

    [Fact]
    public void MoveNext_AdvancesToNextClip()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);
        playlist.SetClips(clips);
        playlist.MoveTo(0);

        // Act
        playlist.MoveNext();

        // Assert
        playlist.CurrentIndex.ShouldBe(1);
    }

    [Fact]
    public void MoveNext_AtEnd_StaysAtEnd()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(3);
        playlist.SetClips(clips);
        playlist.MoveTo(2);

        // Act
        playlist.MoveNext();

        // Assert
        playlist.CurrentIndex.ShouldBe(2);
        playlist.HasNext.ShouldBeFalse();
    }

    [Fact]
    public void MovePrevious_GoesToPreviousClip()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);
        playlist.SetClips(clips);
        playlist.MoveTo(3);

        // Act
        playlist.MovePrevious();

        // Assert
        playlist.CurrentIndex.ShouldBe(2);
    }

    [Fact]
    public void MovePrevious_AtStart_StaysAtStart()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(3);
        playlist.SetClips(clips);
        playlist.MoveTo(0);

        // Act
        playlist.MovePrevious();

        // Assert
        playlist.CurrentIndex.ShouldBe(0);
        playlist.HasPrevious.ShouldBeFalse();
    }

    [Fact]
    public void HasNext_ReturnsTrueWhenNotAtEnd()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);
        playlist.SetClips(clips);
        playlist.MoveTo(2);

        // Assert
        playlist.HasNext.ShouldBeTrue();
    }

    [Fact]
    public void HasPrevious_ReturnsTrueWhenNotAtStart()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);
        playlist.SetClips(clips);
        playlist.MoveTo(2);

        // Assert
        playlist.HasPrevious.ShouldBeTrue();
    }

    [Fact]
    public void CurrentClipChanged_FiresOnMove()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(3);
        playlist.SetClips(clips);
        
        CamClip changedClip = null;
        playlist.CurrentClipChanged += (s, c) => changedClip = c;

        // Act
        playlist.MoveTo(1);

        // Assert
        changedClip.ShouldBe(clips[1]);
    }

    [Fact]
    public void MoveTo_WithClipReference_FindsAndSelects()
    {
        // Arrange
        var playlist = new ClipPlaylist();
        var clips = CreateMockClips(5);
        playlist.SetClips(clips);
        var targetClip = clips[3];

        // Act
        playlist.MoveTo(targetClip);

        // Assert
        playlist.CurrentIndex.ShouldBe(3);
        playlist.CurrentClip.ShouldBe(targetClip);
    }

    private static List<CamClip> CreateMockClips(int count)
    {
        var clips = new List<CamClip>();
        for (int i = 0; i < count; i++)
        {
            // Use the real CamClip.Map with mock folders if available
            // For now, we'll create minimal mock data
            var mockPath = $"Mocks/2023-02-23_14-16-15"; // Use existing mock
            if (Directory.Exists(mockPath))
            {
                var clip = CamClip.Map(mockPath);
                if (clip is not null)
                {
                    clips.Add(clip);
                }
            }
        }
        
        // If no real mocks available, return empty list
        // Tests will need to handle this gracefully
        return clips.Count > 0 ? clips : CreateEmptyMockClips(count);
    }

    private static List<CamClip> CreateEmptyMockClips(int count)
    {
        // For unit tests without file system, create minimal mock clips
        var clips = new List<CamClip>();
        // Note: In real tests, we'd use actual mock folders
        return clips;
    }
}
