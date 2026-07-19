using Sandbox;
using Sandbox.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ambi.MelonRacer;

public sealed class RaceLeaderboardEntry
{
	public long Rank { get; }
	public long SteamId { get; }
	public string DisplayName { get; }
	public double Time { get; }

	public RaceLeaderboardEntry( long rank, long steamId, string displayName, double time )
	{
		Rank = rank;
		SteamId = steamId;
		DisplayName = displayName;
		Time = time;
	}
}

public static class RaceLeaderboardService
{
	public const int MaxEntries = 10;
	public const int MaxDisplayNameLength = 16;

	private static Task _pendingStatsFlush = Task.CompletedTask;

	public static void SubmitLocalResult( string mapFullName, double seconds )
	{
		if ( string.IsNullOrWhiteSpace( mapFullName ) || seconds <= 0d )
			return;

		Stats.SetValue( mapFullName, seconds );
		_pendingStatsFlush = FlushStatsAsync( mapFullName );
	}

	public static async Task<IReadOnlyList<RaceLeaderboardEntry>> GetTopAsync( string mapFullName )
	{
		if ( string.IsNullOrWhiteSpace( mapFullName ) )
			return Array.Empty<RaceLeaderboardEntry>();

		await _pendingStatsFlush;

		var board = Leaderboards.GetFromStat( Game.Ident, mapFullName );
		board.SetAggregationMin();
		board.SetSortAscending();
		board.Offset = 0;
		board.MaxEntries = MaxEntries;

		await board.Refresh();

		var entries = board.Entries ?? Array.Empty<Leaderboards.Board2.Entry>();

		return entries
			.Take( MaxEntries )
			.Select( entry => new RaceLeaderboardEntry(
				entry.Rank,
				entry.SteamId,
				entry.DisplayName,
				entry.Value ) )
			.ToArray();
	}

	public static string FormatTime( double seconds )
	{
		var time = TimeSpan.FromSeconds( Math.Max( 0d, seconds ) );
		return $"{time.Minutes:00}:{time.Seconds:00}:{time.Milliseconds:000}";
	}

	public static string FormatDisplayName( string displayName )
	{
		if ( string.IsNullOrEmpty( displayName ) || displayName.Length <= MaxDisplayNameLength )
			return displayName;

		return displayName.Substring( 0, MaxDisplayNameLength );
	}

	private static async Task FlushStatsAsync( string mapFullName )
	{
		try
		{
			await Stats.FlushAndWaitAsync();
		}
		catch ( Exception exception )
		{
			Log.Warning( $"Failed to publish race time for {mapFullName}: {exception.Message}" );
		}
	}
}
