using Godot;

/// <summary>
/// A Node2D with built-in velocity that automatically updates position each frame.
/// Used for networked objects that need velocity-based movement.
/// </summary>
public partial class NetworkedNode2D : Node2D
{
    public Vector2 Velocity = Vector2.Zero;

    public override void _Process(double delta)
    {
        if (Velocity != Vector2.Zero)
        {
            Position += Velocity * (float)delta;
        }
    }
}
