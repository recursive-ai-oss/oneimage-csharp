namespace OneImage.Drawing;

/// <summary>The set operation <see cref="PathBoolean.Boolean"/> applies to the regions two paths enclose.</summary>
public enum BooleanOperation
{
    /// <summary>Everything inside the subject, the clip, or both.</summary>
    Union = 0,

    /// <summary>Only what is inside both the subject and the clip.</summary>
    Intersection = 1,

    /// <summary>What is inside the subject but not inside the clip.</summary>
    Difference = 2,

    /// <summary>What is inside exactly one of the subject and the clip.</summary>
    Xor = 3,
}
