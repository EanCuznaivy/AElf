using System;
using System.Collections.Generic;
using System.Linq;
using AElf.ChainController;
using AElf.Kernel;
using AElf.Kernel.Node;
using AElf.Network;
using AElf.Node.Protocol;
using NLog;
using NServiceKit.Common;
using NServiceKit.Text;

namespace AElf.Node
{
    /// <summary>
    /// Provide some functions to operate pending blocks and branched chains.
    /// </summary>
    public class BlockCollection : IBlockCollection
    {
        private bool _isInitialSync = true;

        private Hash _chainId;

        private Hash ChainId
        {
            get
            {
                if (_chainId == null)
                {
                    _chainId = Globals.CurrentChainId.Clone();
                }

                return _chainId;
            }
        }

        private IBlockChain _blockChain;
        private IBlockChain BlockChain => _blockChain ?? (_blockChain = _chainService.GetBlockChain(ChainId));

        /// <summary>
        /// To store branched chains.
        /// </summary>
        private readonly HashSet<BranchedChain> _branchedChains = new HashSet<BranchedChain>();

        /// <summary>
        /// To track the latest block height of local chain.
        /// </summary>
        public ulong PendingBlockHeight { get; set; }

        public ulong SyncedHeight =>
            _chainService.GetBlockChain(ChainId).GetCurrentBlockHeightAsync().Result;

        public List<PendingBlock> PendingBlocks { get; set; } = new List<PendingBlock>();

        public int Count => PendingBlocks.Count;
        public int BranchedChainsCount => _branchedChains.Count;

        private readonly ILogger _logger;
        private readonly IChainService _chainService;

        public BlockCollection(IChainService chainService, ILogger logger = null)
        {
            _chainService = chainService;
            _logger = logger;

            _heightBefore = BlockChain.GetCurrentBlockHeightAsync().Result;
        }

        private readonly HashSet<ulong> _initialSyncBlocksIndexes = new HashSet<ulong>();

        private readonly ulong _heightBefore;

        public bool ReceivedAllTheBlocksBeforeTargetBlock => (ulong) _initialSyncBlocksIndexes.Count + _heightBefore == _targetHeight;

        private ulong _targetHeight = ulong.MaxValue;

        /// <summary>
        /// Basically add the pending block if the block is supposed to be on local chain.
        /// Otherwise add the pending block to branched chains.
        /// </summary>
        /// <param name="pendingBlock"></param>
        public List<Transaction> AddPendingBlock(PendingBlock pendingBlock)
        {
            // No need to handle an already exists pending block again.
            if (!PendingBlocks.IsNullOrEmpty() &&
                PendingBlocks.Any(b => new Hash(b.Block.GetHash()) == new Hash(pendingBlock.Block.GetHash())))
            {
                return null;
            }

            if (Globals.IsConsensusGenerator)
            {
                _isInitialSync = false;
            }

            if (_isInitialSync)
            {
                switch (pendingBlock.MsgType)
                {
                    case AElfProtocolMsgType.NewBlock:
                        if (_targetHeight == ulong.MaxValue)
                        {
                            _targetHeight = pendingBlock.Block.Header.Index;
                            if (_targetHeight == _heightBefore + 1)
                            {
                                _isInitialSync = false;
                            }
                            AddToPendingBlocks(pendingBlock);
                            PendingBlocks.SortByBlockIndex();
                            _initialSyncBlocksIndexes.Add(_targetHeight);
                            return null;
                        }
                        else
                        {
                            _logger?.Trace("Receive a new block while do initial sync.");
                            return AddBlockToBranchedChains(pendingBlock);
                        }

                    case AElfProtocolMsgType.Block:
                        if (!_initialSyncBlocksIndexes.Contains(pendingBlock.Block.Header.Index) &&
                            !ReceivedAllTheBlocksBeforeTargetBlock)
                        {
                            AddToPendingBlocks(pendingBlock);
                            _initialSyncBlocksIndexes.Add(pendingBlock.Block.Header.Index);
                            if (ReceivedAllTheBlocksBeforeTargetBlock)
                            {
                                _isInitialSync = false;
                            }

                            return null;
                        }
                        else
                        {
                            _logger?.Trace("Receive a forked block while do initial sync.");
                            return AddBlockToBranchedChains(pendingBlock);
                        }
                }

                return null;
            }

            if (!AbleToAdd(pendingBlock))
            {
                _logger?.Trace("Receive an orphan block.");
                return AddBlockToBranchedChains(pendingBlock);
            }

            AddToPendingBlocks(pendingBlock);
            return null;
        }

        private bool AbleToAdd(PendingBlock pendingBlock)
        {
            switch (pendingBlock.MsgType)
            {
                case AElfProtocolMsgType.Block:
                    return false;
                case AElfProtocolMsgType.NewBlock:
                    if (PendingBlocks.IsEmpty())
                    {
                        return SyncedHeight + 1 == pendingBlock.Block.Header.Index &&
                               pendingBlock.Block.Header.PreviousBlockHash ==
                               BlockChain.GetCurrentBlockHashAsync().Result;
                    }

                    var lastPendingBlock = PendingBlocks.Last().Block;
                    return pendingBlock.Block.Header.Index == lastPendingBlock.Header.Index + 1
                           && pendingBlock.Block.Header.PreviousBlockHash == lastPendingBlock.Header.GetHash();
                default:
                    return false;
            }
        }

        private void AddToPendingBlocks(PendingBlock pendingBlock)
        {
            PendingBlockHeight = Math.Max(PendingBlockHeight, pendingBlock.Block.Header.Index);
            _logger?.Trace("Adding to pending blocks: " + pendingBlock.Block.GetHash().ToHex());
            PrintPendingBlocks(PendingBlocks);
            PendingBlocks.Add(pendingBlock);
            PendingBlocks.SortByBlockIndex();
        }

        /// <summary>
        /// Add the pending block to branched chain after removing.
        /// </summary>
        /// <param name="pendingBlock"></param>
        public void RemovePendingBlock(PendingBlock pendingBlock)
        {
            if (pendingBlock.ValidationError == ValidationError.Success)
            {
                PendingBlocks.Remove(pendingBlock);
            }
            else
            {
                _logger?.Trace("ValidationError: " + pendingBlock.ValidationError);
                PendingBlocks.Remove(pendingBlock);
                AddBlockToBranchedChains(pendingBlock);
            }

            if (PendingBlocks.IsEmpty() && BranchedChainsCount > 0)
            {
                PendingBlocks = _branchedChains.FirstOrDefault(c => c.CanCheckout(PendingBlockHeight))
                                    ?.GetPendingBlocks() ??
                                _branchedChains.FirstOrDefault()?.GetPendingBlocks();
            }
        }

        private List<Transaction> AddBlockToBranchedChains(PendingBlock pendingBlock)
        {
            PrintPendingBlocks(PendingBlocks);

            _logger?.Trace(
                $"Ready to add pending block height: {pendingBlock.Block.Header.Index}\nBlock number of each round: {Globals.BlockNumberOfEachRound}\nPending block height or Synced height: {(PendingBlockHeight == 0 ? SyncedHeight : PendingBlockHeight)}");
            if (pendingBlock.Block.Header.Index + (ulong) Globals.BlockNumberOfEachRound < (PendingBlockHeight == 0 ? SyncedHeight : PendingBlockHeight))
            {
                return null;
            }

            _logger?.Trace(
                $"Adding to branched chain: {pendingBlock.Block.GetHash().ToHex()} : {pendingBlock.Block.Header.Index}");

            if (_branchedChains.Count == 0)
            {
                _branchedChains.Add(new BranchedChain(pendingBlock));
                return null;
            }

            var preBlockHash = pendingBlock.Block.Header.PreviousBlockHash;
            var blockHash = pendingBlock.Block.Header.GetHash();

            var toRemove = new List<BranchedChain>();
            var toAdd = new List<BranchedChain>();

            foreach (var branchedChain in _branchedChains)
            {
                if (branchedChain.GetPendingBlocks().First().Block.Header.PreviousBlockHash == blockHash)
                {
                    var newBranchedChain = new BranchedChain(pendingBlock, branchedChain.GetPendingBlocks());
                    toAdd.Add(newBranchedChain);
                    toRemove.Add(branchedChain);
                }
                else if (branchedChain.GetPendingBlocks().Last().Block.GetHash() == preBlockHash)
                {
                    var newBranchedChain = new BranchedChain(branchedChain.GetPendingBlocks(), pendingBlock);
                    toAdd.Add(newBranchedChain);
                    toRemove.Add(branchedChain);
                }
                else
                {
                    if (toAdd.Any(c => c.GetPendingBlocks().Any(pd => pd.Block.GetHash() == blockHash)) ||
                        _branchedChains.Any(bc => bc.LastBlockHash == blockHash) ||
                        _branchedChains.Any(bc => bc.GetPendingBlocks().Any(pb => pb.Block.GetHash() == blockHash)))
                    {
                        continue;
                    }

                    toAdd.Add(new BranchedChain(pendingBlock));
                }
            }

            foreach (var branchedChain in toRemove)
            {
                _branchedChains.Remove(branchedChain);
            }

            foreach (var branchedChain in toAdd)
            {
                _branchedChains.Add(branchedChain);
            }

            _logger?.Trace("Branched chains count: " + BranchedChainsCount);

            var flag = 1;
            foreach (var branchedChain in _branchedChains)
            {
                _logger?.Trace(flag++ + ":");
                PrintPendingBlocks(branchedChain.GetPendingBlocks());
            }

            var result = AdjustBranchedChains();
            if (result == null)
                return null;

            if (DPoS.ConsensusDisposable != null)
            {
                DPoS.ConsensusDisposable.Dispose();
                _logger?.Trace("Disposed previous consensus observables list.");
            }
            
            if (SyncedHeight < result.StartHeight)
            {
                var oldBlocks = new List<PendingBlock>();
                // Replace the pending blocks with the result
                foreach (var branchedBlock in result.GetPendingBlocks())
                {
                    if (PendingBlockHeight >= branchedBlock.Block.Header.Index)
                    {
                        var corresponding =
                            PendingBlocks.First(pb => pb.Block.Header.Index == branchedBlock.Block.Header.Index);
                        PendingBlocks.Remove(corresponding);
                        oldBlocks.Add(corresponding);
                    }

                    PendingBlocks.Add(branchedBlock);
                }

                if (!oldBlocks.IsEmpty())
                {
                    //TODO: Move back.
                    //_branchedChains.Add(new BranchedChain(oldBlocks));
                }
            }
            else
            {
                PendingBlocks = result.GetPendingBlocks();
            }

            PendingBlockHeight = result.EndHeight;
            _branchedChains.Remove(result);

            // State rollback.
            _logger?.Trace("Rollback to height: " + (result.StartHeight - 1));
            var txs = BlockChain.RollbackToHeight(result.StartHeight - 1).Result;

            return txs;
        }

        private BranchedChain AdjustBranchedChains()
        {
            _branchedChains.RemoveWhere(bc =>
                bc.StartHeight + (ulong) Globals.BlockNumberOfEachRound < (PendingBlockHeight == 0 ? SyncedHeight : PendingBlockHeight));

            var preBlockHashes = new List<Hash>();
            var lastBlockHashes = new List<Hash>();
            foreach (var branchedChain in _branchedChains)
            {
                preBlockHashes.Add(branchedChain.PreBlockHash);
                lastBlockHashes.Add(branchedChain.LastBlockHash);
            }

            var same = new List<Hash>();
            foreach (var preBlockHash in preBlockHashes)
            {
                foreach (var lastBlockHash in lastBlockHashes)
                {
                    if (preBlockHash == lastBlockHash)
                    {
                        same.Add(preBlockHash);
                    }
                }
            }

            foreach (var hash in same)
            {
                var chain1 = _branchedChains.First(c => c.PreBlockHash == hash);
                var chain2 = _branchedChains.First(c => c.LastBlockHash == hash);
                _branchedChains.Remove(chain1);
                _branchedChains.Remove(chain2);
                _branchedChains.Add(new BranchedChain(chain1.GetPendingBlocks(), chain2.GetPendingBlocks()));
            }

            foreach (var branchedChain in _branchedChains)
            {
                if (branchedChain.CanCheckout(PendingBlockHeight))
                {
                    _logger?.Trace("Switching chain.");
                    return branchedChain;
                }
            }

            return null;
        }

        public List<PendingBlock> GetPendingBlocksFromBranchedChains()
        {
            var pendingBlocks = new List<PendingBlock>();

            foreach (var chain in _branchedChains)
            {
                pendingBlocks.AddRange(chain.GetPendingBlocks());
            }

            return pendingBlocks;
        }

        private void PrintPendingBlocks(List<PendingBlock> pendingBlocks)
        {
            if (pendingBlocks.IsNullOrEmpty())
            {
                _logger?.Trace("Current PendingBlocks list is empty.");
            }
            else
            {
                _logger?.Trace("Current PendingBlocks:");
                foreach (var pendingBlock in pendingBlocks)
                {
                    _logger?.Trace($"{pendingBlock.Block.GetHash().ToHex()} - {pendingBlock.Block.Header.Index}");
                }
            }
        }
    }
}