public class HashedSlotArray
{
	private const int ARRAY_SIZE = 4096;
	private const int INDEX_MASK = 0x0FFF; // For fast modulo via bitwise AND (12 bits, top 4 reserved for flags)

	private ulong[] slots = new ulong[ARRAY_SIZE];
	private bool[] occupied = new bool[ARRAY_SIZE]; // Track which slots are in use

	/// <summary>
	/// Inserts a ulong value using the value itself as a hash for initial placement.
	/// Returns the index where stored, or -1 if array is full.
	/// </summary>
	public short Insert(ulong value)
	{
		// Hash: XOR all 12-bit chunks together for better distribution
		short startIndex = (short)((value ^ (value >> 12) ^ (value >> 24) ^ (value >> 36) ^ (value >> 48)) & INDEX_MASK);
		short currentIndex = startIndex;

		do
		{
			if (!occupied[currentIndex])
			{
				slots[currentIndex] = value;
				occupied[currentIndex] = true;
				return currentIndex;
			}

			// Linear probe with wraparound
			currentIndex = (short)((currentIndex + 1) & INDEX_MASK);
		}
		while (currentIndex != startIndex);

		// Array is full
		return -1;
	}

	/// <summary>
	/// Removes the value at the given index.
	/// </summary>
	public void RemoveAt(short index)
	{
		occupied[index] = false;
	}

	/// <summary>
	/// Gets the value at the given index.
	/// </summary>
	public ulong GetAt(short index)
	{
		return slots[index];
	}

	/// <summary>
	/// Inserts a ulong value at a specific index.
	/// Used on the client side to match server-assigned indices.
	/// </summary>
	public void InsertAt(short index, ulong value)
	{
		slots[index] = value;
		occupied[index] = true;
	}

	public bool IsOccupied(short index)
	{
		return occupied[index];
	}

	/// <summary>
	/// Finds the index of a given ulong value in the array.
	/// Returns the index where found, or -1 if not present.
	/// </summary>
	public short Find(ulong value)
	{
		// Hash: XOR all 12-bit chunks together for better distribution
		short startIndex = (short)((value ^ (value >> 12) ^ (value >> 24) ^ (value >> 36) ^ (value >> 48)) & INDEX_MASK);
		short currentIndex = startIndex;

		do
		{
			if (occupied[currentIndex] && slots[currentIndex] == value)
			{
				return currentIndex;
			}

			// If we hit an unoccupied slot, the value isn't in the array
			if (!occupied[currentIndex])
			{
				return -1;
			}

			// Linear probe with wraparound
			currentIndex = (short)((currentIndex + 1) & INDEX_MASK);
		}
		while (currentIndex != startIndex);

		// Wrapped all the way around, not found
		return -1;
	}
}
