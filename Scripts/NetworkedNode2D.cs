using Godot;

/// <summary>
/// A Node2D with built-in velocity that automatically updates position each frame.
/// Used for networked objects that need velocity-based movement.
/// </summary>
public partial class NetworkedNode2D : Node2D
{
    public Vector2 Velocity = Vector2.Zero;
    private short _soundToPlay = -1;
    private float timeToResetSound = 0;
    public short SoundToPlay
    {
        get => _soundToPlay;
        set
        {
            _soundToPlay = value;
            if (value != -1)
            {
                // Your code here - e.g., play the sound
                AudioStream streamToPlay = Globals.worldManager_server.networkManager_server.LoadedSounds[_soundToPlay];
                timeToResetSound = (Time.GetTicksMsec() / 1000.0f) + (float)streamToPlay.GetLength();
            }
        }
    }

    public bool SoundIs2D = false;

    public override void _Process(double delta)
    {
        if (Velocity != Vector2.Zero)
        {
            Position += Velocity * (float)delta;
        }

        // if a sound has finished playing, then reset us back to -1 for the sound being played.
        float now = (Time.GetTicksMsec() / 1000.0f);
        if (timeToResetSound != 0 && timeToResetSound < now)
        {
            timeToResetSound = 0;
            _soundToPlay = -1;
            SoundIs2D = false;
        }
    }
}
