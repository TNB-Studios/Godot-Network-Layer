using Godot;

/// <summary>
/// A Node3D with built-in velocity that automatically updates position each frame.
/// Used for networked objects that need velocity-based movement.
/// </summary>
public partial class NetworkedNode3D : Node3D
{
    public Vector3 Velocity = Vector3.Zero;
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
    public bool CompresedVelocityAndOrientation = false;

    public int currentModelIndex = -1;
    public int currentAnimationIndex = -1;
    public int currentParticleEffectIndex = -1;

    /// <summary>
    /// Sets the model for this networked node by index into the loaded models list.
    /// Removes any existing model children and instantiates the new model.
    /// </summary>
    public void SetModel(int modelIndex)
    {
        currentModelIndex = modelIndex;
        if (modelIndex < 0) return;

        // Remove any existing model children (nodes with a SceneFilePath)
        foreach (Node child in GetChildren())
        {
            if (!string.IsNullOrEmpty(child.SceneFilePath))
            {
                child.QueueFree();
            }
        }

        // Instantiate and add the new model
        Node3D modelInstance = Globals.worldManager_server.networkManager_server.LoadedModels[modelIndex].Instantiate<Node3D>();
        AddChild(modelInstance);
    }

    /// <summary>
    /// Sets the animation for this networked node by index into the loaded animations list.
    /// </summary>
    public void SetAnimation(int animationIndex)
    {
        currentAnimationIndex = animationIndex;
        // TODO: Apply animation when animation system is implemented
    }

    /// <summary>
    /// Sets the particle effect for this networked node by index into the loaded particle effects list.
    /// </summary>
    public void SetParticleEffect(int particleEffectIndex)
    {
        currentParticleEffectIndex = particleEffectIndex;
        // TODO: Apply particle effect when particle system is implemented
    }

    public float SoundRadius = 50.0f;
    public bool SoundIs2D = false;
    public override void _Process(double delta)
    {
        if (Velocity != Vector3.Zero)
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
