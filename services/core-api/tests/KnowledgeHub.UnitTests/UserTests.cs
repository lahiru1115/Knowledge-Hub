using KnowledgeHub.Domain;
using Xunit;

namespace KnowledgeHub.UnitTests;

public class UserTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan s_window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_lock = TimeSpan.FromMinutes(15);

    [Fact]
    public void Ten_failures_within_the_window_lock_the_account_for_fifteen_minutes()
    {
        var user = new User();
        for (var i = 0; i < 9; i++) user.RegisterFailedLogin(s_t0.AddSeconds(i), 10, s_window, s_lock);
        Assert.False(user.IsLocked(s_t0.AddSeconds(9)));

        user.RegisterFailedLogin(s_t0.AddSeconds(9), 10, s_window, s_lock);

        Assert.True(user.IsLocked(s_t0.AddMinutes(14)));
        Assert.False(user.IsLocked(s_t0.AddMinutes(15).AddSeconds(10)));
    }

    [Fact]
    public void Failures_spread_beyond_the_window_do_not_lock()
    {
        var user = new User();
        for (var i = 0; i < 20; i++) user.RegisterFailedLogin(s_t0.AddMinutes(i * 16), 10, s_window, s_lock);
        Assert.False(user.IsLocked(s_t0.AddMinutes(20 * 16)));
    }

    [Fact]
    public void A_successful_login_clears_the_failure_state_and_records_the_time()
    {
        var user = new User();
        for (var i = 0; i < 5; i++) user.RegisterFailedLogin(s_t0, 10, s_window, s_lock);

        user.RegisterSuccessfulLogin(s_t0.AddMinutes(1));

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.Equal(s_t0.AddMinutes(1), user.LastLoginAt);
    }

    [Fact]
    public void New_users_get_time_ordered_ids_and_default_preferences()
    {
        var a = new User();
        var b = new User();
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(7, a.Id.Version);
        Assert.True(a.Preferences.AiAutoSummary);
        Assert.Equal(ThemePreference.System, a.Preferences.Theme);
    }
}
