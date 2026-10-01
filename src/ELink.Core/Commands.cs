using ELink.Contracts.Equipment;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Core;

/// <summary>Calling command functions and folding dRPC's "one answer per provider" into a single result.</summary>
public static class Commands
{
    /// <summary>Calls a command; ok only if at least one provider answered and every answer is ok.</summary>
    public static async Task<CommandResult> CallAsync<TIn>(TypeSafeEVentNode node, string id, TIn input, TimeSpan? timeout = null)
        where TIn : IBinaryConvertible, new()
    {
        try
        {
            var answers = await node.CallFunctionAsync<TIn, CommandResult>(id, input, timeout);
            if (answers is null) return CommandResult.Fail($"{id}: type mismatch");
            if (answers.Count == 0) return CommandResult.Fail($"nobody provides {id}");
            foreach (var a in answers) if (!a.Ok.Value) return a;
            return CommandResult.Success();
        }
        catch (ObjectDisposedException) { return CommandResult.Fail("node stopped"); }
    }
}
