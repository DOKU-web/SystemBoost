// A driver frame limit on the game. A game that fills the GPU leaves frame generation no room, and
// shader work cannot win that back: what frees the GPU is the game asking for less of it. The limit
// lives in the graphics driver's per-game settings; the game's process and files are not touched.
using System.Threading.Tasks;

namespace SystemBoost.App.Services;

public interface IGameLimiter
{
    /// Null when the limit is on, otherwise the text to show. exe is a bare file name.
    Task<string?> LimitGameAsync(string exe, int fps);
    /// Takes the limit off again, whichever game has it.
    Task EndGameLimitAsync();
}
