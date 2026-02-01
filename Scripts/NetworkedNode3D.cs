using Godot;

/// <summary>
/// A Node3D with built-in velocity that automatically updates position each frame.
/// Used for networked objects that need velocity-based movement.
/// </summary>
public partial class NetworkedNode3D : Node3D
{
    public Vector3 Velocity = Vector3.Zero;

    public override void _Process(double delta)
    {
        if (Velocity != Vector3.Zero)
        {
            Position += Velocity * (float)delta;
        }
    }
}
