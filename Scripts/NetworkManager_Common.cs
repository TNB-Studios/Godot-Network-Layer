using System.Collections.Generic;
using Godot;


public class NetworkManager_Common
{
	// Reference nodes for scene tree access
	protected Node _rootSceneNode;

	// NOTE - On the server these need to be precached BEFORE we start the game; they cannot be changed post game start, since they are transmitted
	// to all clients on each Client being initialized. Also, they can't ba larger than 65536 in size, but that should be enough for anyone - this is the max index size in individual SharedProperties
	public List<string> SoundsUsed { get; set; } = new List<string>();
	public List<string> ModelsUsed { get; set; } = new List<string>();
	public List<string> AnimationsUsed { get; set; } = new List<string>();
	public List<string> ParticleEffectsUsed {get; set;} = new List<string>();


	protected HashedSlotArray IDToNetworkIDLookup;

    protected static readonly string NETWORKED_GROUP_NAME = "networked";

    public NetworkManager_Common(Node rootSceneNode)
	{
		_rootSceneNode = rootSceneNode;
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
	
}
