using System.ComponentModel.DataAnnotations;

namespace UpskillTracker.Models;

public class GitHubAuthSession
{
    [Key]
    [MaxLength(64)]
    public string SessionId { get; set; } = string.Empty;

    [MaxLength(80)]
    public string GitHubLogin { get; set; } = string.Empty;

    [MaxLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string AvatarUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string ProtectedAccessToken { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresUtc { get; set; }

    public DateTime LastAccessedUtc { get; set; } = DateTime.UtcNow;
}