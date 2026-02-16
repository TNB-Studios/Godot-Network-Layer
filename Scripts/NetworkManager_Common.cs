using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;

/// <summary>
/// Packet types sent from client to server.
/// First byte of any client->server packet identifies the type.
/// </summary>
public enum PlayerSentPacketTypes : byte
{
	INITIATING_TCP_ACK = 0,
	PLAYER_INPUT = 1
}

/// <summary>
/// Delivery method for packets.
/// </summary>
public enum PacketDeliveryMethod
{
	Reliable,   // TCP-like, guaranteed delivery
	Unreliable  // UDP-like, fire and forget
}

public class NetworkManager_Common
{
	// Reference nodes for scene tree access
	protected Node _rootSceneNode;

	public Node RootSceneNode => _rootSceneNode;

	// NOTE - On the server these need to be precached BEFORE we start the game; they cannot be changed post game start, since they are transmitted
	// to all clients on each Client being initialized. Also, they can't be larger than 65536 in size, but that should be enough for anyone - this is the max index size in individual SharedProperties
	public List<string> SoundNames { get; set; } = new List<string>();
	public List<string> ModelNames { get; set; } = new List<string>();
	public List<string> AnimationNames { get; set; } = new List<string>();
	public List<string> ParticleEffectNames { get; set; } = new List<string>();

	// Loaded/cached resources
	public List<PackedScene> LoadedModels { get; set; } = new List<PackedScene>();
	public List<AudioStream> LoadedSounds { get; set; } = new List<AudioStream>();
	public List<PackedScene> LoadedParticleEffects { get; set; } = new List<PackedScene>();

	public HashedSlotArray IDToNetworkIDLookup;

    protected static readonly string NETWORKED_GROUP_NAME = "networked";

    public NetworkManager_Common(Node rootSceneNode)
	{
		_rootSceneNode = rootSceneNode;
	}

	/// <summary>
	/// Loads all models from ModelNames into LoadedModels.
	/// Call this after ModelNames has been populated.
	/// </summary>
	public void LoadModelsFromNames()
	{
		LoadedModels = new List<PackedScene>(ModelNames.Count);

		int index = 0;
		foreach (string modelName in ModelNames)
		{
			try
			{
				if (modelName != null && modelName != "")
				{
					PackedScene scene = GD.Load<PackedScene>("res://" + modelName);
					LoadedModels.Add(scene);
				}
				else
				{
					GD.Print("Cannot find model " + modelName);
					LoadedModels.Add(null);
				}
			}
			catch { }
			index++;
		}		
	}

	/// <summary>
	/// Loads all sounds from SoundNames into LoadedSounds.
	/// Call this after SoundNames has been populated.
	/// </summary>
	public void LoadSoundsFromNames()
	{
        LoadedSounds = new List<AudioStream>(SoundNames.Count);

        int index = 0;
        foreach (string soundName in SoundNames)
        {
            try
            {
				if (soundName != null && soundName != "")
				{
					AudioStream sound = GD.Load<AudioStream>("res://" + soundName);
					LoadedSounds.Add(sound);
				}
                else
                {
                    GD.Print("Cannot sound model " + soundName);
                    LoadedSounds.Add(null);
                }
            }
            catch { }
            index++;
        }
	}

	/// <summary>
	/// Loads all particle effects from ParticleEffectNames into LoadedParticleEffects.
	/// Call this after ParticleEffectNames has been populated.
	/// </summary>
	public void LoadParticleEffectsFromNames()
	{
        LoadedParticleEffects = new List<PackedScene>(ParticleEffectNames.Count);

        int index = 0;
        foreach (string particleEffect in ParticleEffectNames)
        {
            try
            {
				if (particleEffect != null && particleEffect != "")
				{
					PackedScene scene = GD.Load<PackedScene>("res://" + particleEffect);
					LoadedParticleEffects.Add(scene);
                }
                else
                {
                    GD.Print("Cannot sound particle Effect " + particleEffect);
                    LoadedParticleEffects.Add(null);
                }

            }
            catch { }
            index++;
        }
    }

	/// <summary>
	/// Writes a 3-byte integer to a buffer (little-endian).
	/// </summary>
	protected unsafe void WriteInt24ToBuffer(byte* bufferPtr, int offset, int value)
	{
		bufferPtr[offset] = (byte)(value & 0xFF);
		bufferPtr[offset + 1] = (byte)((value >> 8) & 0xFF);
		bufferPtr[offset + 2] = (byte)((value >> 16) & 0xFF);
	}

	/// <summary>
	/// Reads a 3-byte integer from a buffer (little-endian).
	/// </summary>
	protected unsafe int ReadInt24FromBuffer(byte* bufferPtr, int offset)
	{
		return bufferPtr[offset] | (bufferPtr[offset + 1] << 8) | (bufferPtr[offset + 2] << 16);
	}

	protected static byte[] CompressPayload(byte[] data, int offset, int count)
	{
		using (var output = new MemoryStream())
		{
			using (var deflate = new DeflateStream(output, CompressionLevel.Fastest))
			{
				deflate.Write(data, offset, count);
			}
			return output.ToArray();
		}
	}

	protected static byte[] DecompressPayload(byte[] data, int offset, int count)
	{
		using (var input = new MemoryStream(data, offset, count))
		using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
		using (var output = new MemoryStream())
		{
			deflate.CopyTo(output);
			return output.ToArray();
		}
	}
}
