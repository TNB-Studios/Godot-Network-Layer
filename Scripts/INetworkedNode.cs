using Godot;
using System.Collections.Generic;

public interface INetworkedNode
{
    short SoundToPlay { get; set; }
    float SoundRadius { get; set; }
    bool SoundIs2D { get; set; }
    float TimeToResetSound { get; set; }
    short currentModelIndex { get; set; }
    short currentAnimationIndex { get; set; }
    short currentParticleEffectIndex { get; set; }
    short attachedToObjectLookupIndex { get; set; }
    bool CompressedVelocityAndOrientation { get; set; }

    void SetSound(short soundIndex, List<AudioStream> loadedSounds,
                  float soundRadius = 50.0f, bool soundIs2D = false, bool serverSide = true);
    void SetAnimation(short animationIndex);
    void SetModel(short modelIndex, List<PackedScene> loadedModels);
    void SetParticleEffect(short particleEffectIndex, List<PackedScene> loadedParticleEffects, bool serverSide = true);
}
