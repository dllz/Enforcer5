using System;

namespace Enforcer5.Models
{
    /// <summary>A user under a restriction that ends at <paramref name="UntilUtc"/>: a tempmute or tempban.</summary>
    internal sealed record TimedRestriction(long UserId, DateTime UntilUtc);
}
