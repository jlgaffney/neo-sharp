using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NeoSharp.BinarySerialization;
using NeoSharp.Core.Cryptography;
using NeoSharp.Core.Models;
using NeoSharp.Core.Persistence;
using NeoSharp.Persistence.RedisDB.Helpers;
using NeoSharp.Types;
using StackExchange.Redis;

namespace NeoSharp.Persistence.RedisDB
{
    public class RedisDbBinaryRepository : IRepository
    {
        #region Private Fields

        private readonly IRedisDbContext _redisDbContext;
        private readonly IBinarySerializer _binarySerializer;

        private readonly string _sysCurrentBlockKey = DataEntryPrefix.SysCurrentBlock.ToString();
        private readonly string _sysCurrentBlockHeaderKey = DataEntryPrefix.SysCurrentHeader.ToString();
        private readonly string _sysCurrentTransactionKey = DataEntryPrefix.SysCurrentTransaction.ToString();
        private readonly string _sysVersionKey = DataEntryPrefix.SysVersion.ToString();
        private readonly string _indexHeightKey = DataEntryPrefix.IxIndexHeight.ToString();
        private readonly string _stValidatorPublicKeys = DataEntryPrefix.StValidatorPublicKeys.ToString();

        #endregion

        #region Constructor

        public RedisDbBinaryRepository
        (
            IRedisDbContext redisDbContext,
            IBinarySerializer binarySerializer
        )
        {
            _redisDbContext = redisDbContext ?? throw new ArgumentNullException(nameof(redisDbContext));
            _binarySerializer = binarySerializer ?? throw new ArgumentNullException(nameof(binarySerializer));
        }

        #endregion

        #region IRepository System Members

        public async Task<uint> GetTotalBlockHeight()
        {
            var val = await _redisDbContext.Get(_sysCurrentBlockKey);
            return val == RedisValue.Null ? uint.MinValue : (uint) val;
        }

        public async Task SetTotalBlockHeight(uint height)
        {
            await _redisDbContext.Set(_sysCurrentBlockKey, height);
        }

        public async Task<uint> GetTotalBlockHeaderHeight()
        {
            var val = await _redisDbContext.Get(_sysCurrentBlockHeaderKey);
            return val == RedisValue.Null ? uint.MinValue : (uint) val;
        }

        public async Task SetTotalBlockHeaderHeight(uint height)
        {
            await _redisDbContext.Set(_sysCurrentBlockHeaderKey, height);
        }

        public async Task<string> GetVersion()
        {
            var val = await _redisDbContext.Get(_sysVersionKey);
            return val == RedisValue.Null ? null : (string) val;
        }

        public async Task SetVersion(string version)
        {
            await _redisDbContext.Set(_sysVersionKey, version);
        }

        #endregion

        #region IRepository Data Members

        public async Task AddBlockHeader(BlockHeader blockHeader)
        {
            var blockHeaderBytes = _binarySerializer.Serialize(blockHeader);
            await _redisDbContext.Set(blockHeader.Hash.BuildDataBlockKey(), blockHeaderBytes);

            await _redisDbContext.AddToIndex(RedisIndex.BlockTimestamp, blockHeader.Hash, blockHeader.Timestamp);
            await _redisDbContext.AddToIndex(RedisIndex.BlockHeight, blockHeader.Hash, blockHeader.Index);
        }

        public async Task AddTransaction(Transaction transaction)
        {
            var raw = await _redisDbContext.Get(_sysCurrentTransactionKey);
            var transactionHeight = raw == RedisValue.Null ? uint.MinValue : (uint)raw;

            var transactionBytes = _binarySerializer.Serialize(transaction);
            await _redisDbContext.Set(transaction.Hash.BuildDataTransactionKey(), transactionBytes);

            transactionHeight += 1u;

            await _redisDbContext.Set(_sysCurrentTransactionKey, transactionHeight);
            await _redisDbContext.Set(transaction.Hash.BuildDataTransactionKey(), transactionHeight);
        }

        public async Task<uint> GetTransactionHeightFromHash(UInt256 hash)
        {
            var rawHeight = await _redisDbContext.Get(hash.BuildTransactionHashToHeightKey());
            return rawHeight == RedisValue.Null ? uint.MinValue : (uint)rawHeight;
        }

        public async Task<UInt256> GetBlockHashFromHeight(uint height)
        {
            var hash = await _redisDbContext.GetFromHashIndex(RedisIndex.BlockHeight, height);
            return hash ?? UInt256.Zero;
        }

        public async Task<IEnumerable<UInt256>> GetBlockHashesFromHeights(IEnumerable<uint> heights)
        {
            var hashes = await Task.WhenAll(heights.Select(GetBlockHashFromHeight));
            return hashes;
        }

        public async Task<BlockHeader> GetBlockHeader(UInt256 hash)
        {
            var blockHeaderRedisValue = await _redisDbContext.Get(hash.BuildDataBlockKey());
            return blockHeaderRedisValue.IsNull ? null : _binarySerializer.Deserialize<BlockHeader>(blockHeaderRedisValue);
        }

        public async Task<Transaction> GetTransaction(UInt256 hash)
        {
            var transactionRedisValue = await _redisDbContext.Get(hash.BuildDataTransactionKey());
            return transactionRedisValue.IsNull ? null : _binarySerializer.Deserialize<Transaction>(transactionRedisValue);
        }

        public async Task<bool> ContainsTransaction(UInt256 hash)
        {
            return await _redisDbContext.Contains(hash.BuildDataTransactionKey());
        }

        #endregion

        #region IRepository State Members

        public async Task<Account> GetAccount(UInt160 hash)
        {
            var raw = await _redisDbContext.Get(hash.BuildStateAccountKey());
            return raw.IsNull ? null : _binarySerializer.Deserialize<Account>(raw);
        }

        public async Task AddAccount(Account acct)
        {
            await _redisDbContext.Set(acct.ScriptHash.BuildStateAccountKey(), _binarySerializer.Serialize(acct));
        }

        public async Task DeleteAccount(UInt160 hash)
        {
            await _redisDbContext.Delete(hash.BuildStateAccountKey());
        }

        public async Task<CoinState[]> GetCoinStates(UInt256 txHash)
        {
            var raw = await _redisDbContext.Get(txHash.BuildStateCoinKey());
            return raw.IsNull ? null : _binarySerializer.Deserialize<CoinState[]>(raw);
        }

        public async Task AddCoinStates(UInt256 txHash, CoinState[] coinStates)
        {
            await _redisDbContext.Set(txHash.BuildStateCoinKey(), _binarySerializer.Serialize(coinStates));
        }

        public async Task DeleteCoinStates(UInt256 txHash)
        {
            await _redisDbContext.Delete(txHash.BuildStateCoinKey());
        }

        public async Task<IEnumerable<Validator>> GetValidators()
        {
            var rawValidatorsPublicKeys = await _redisDbContext.Get(_stValidatorPublicKeys);

            if (rawValidatorsPublicKeys.IsNull)
            {
                return Enumerable.Empty<Validator>();
            }

            var validatorsPublicKeys = _binarySerializer.Deserialize<ECPoint[]>(rawValidatorsPublicKeys);

            var rawValidators = await _redisDbContext.GetMany(validatorsPublicKeys.Select(publicKey => (RedisKey)publicKey.BuildStateValidatorKey()).ToArray());

            var validators = new List<Validator>(rawValidators.Count);

            foreach (var rawValidator in rawValidators.Values)
            {
                if (rawValidator.IsNull)
                {
                    continue;
                }

                var validator = _binarySerializer.Deserialize<Validator>(rawValidator);
                validators.Add(validator);
            }

            return validators;
        }

        public async Task<Validator> GetValidator(ECPoint publicKey)
        {
            var raw = await _redisDbContext.Get(publicKey.BuildStateValidatorKey());
            return raw.IsNull ? null : _binarySerializer.Deserialize<Validator>(raw);
        }

        public async Task AddValidator(Validator validator)
        {
            var rawValidatorsPublicKeys = await _redisDbContext.Get(_stValidatorPublicKeys);

            List<ECPoint> validatorsPublicKeys;
            if (rawValidatorsPublicKeys.IsNull)
            {
                validatorsPublicKeys = new List<ECPoint>(1);
            }
            else
            {
                validatorsPublicKeys = _binarySerializer.Deserialize<ECPoint[]>(rawValidatorsPublicKeys).ToList();
            }

            if (!validatorsPublicKeys.Contains(validator.PublicKey))
            {
                validatorsPublicKeys.Add(validator.PublicKey);
            }

            await _redisDbContext.Set(_stValidatorPublicKeys, _binarySerializer.Serialize(validatorsPublicKeys.ToArray()));
            
            await _redisDbContext.Set(validator.PublicKey.BuildStateValidatorKey(), _binarySerializer.Serialize(validator));
        }

        public async Task DeleteValidator(ECPoint publicKey)
        {
            var rawValidatorsPublicKeys = await _redisDbContext.Get(_stValidatorPublicKeys);

            if (!rawValidatorsPublicKeys.IsNull)
            {
                var validatorsPublicKeys = _binarySerializer.Deserialize<ECPoint[]>(rawValidatorsPublicKeys).ToList();

                if (validatorsPublicKeys.Contains(publicKey))
                {
                    validatorsPublicKeys.Remove(publicKey);
                }

                await _redisDbContext.Set(_stValidatorPublicKeys, _binarySerializer.Serialize(validatorsPublicKeys.ToArray()));
            }

            await _redisDbContext.Delete(publicKey.BuildStateValidatorKey());
        }

        public async Task<Asset> GetAsset(UInt256 assetId)
        {
            var raw = await _redisDbContext.Get(assetId.BuildStateAssetKey());
            return raw.IsNull ? null : _binarySerializer.Deserialize<Asset>(raw);
        }

        public async Task AddAsset(Asset asset)
        {
            await _redisDbContext.Set(asset.Id.BuildStateAssetKey(), _binarySerializer.Serialize(asset));
        }

        public async Task DeleteAsset(UInt256 assetId)
        {
            await _redisDbContext.Delete(assetId.BuildStateAssetKey());
        }

        public async Task<Contract> GetContract(UInt160 contractHash)
        {
            var raw = await _redisDbContext.Get(contractHash.BuildStateContractKey());
            return raw.IsNull ? null : _binarySerializer.Deserialize<Contract>(raw);
        }

        public async Task AddContract(Contract contract)
        {
            await _redisDbContext.Set(contract.ScriptHash.BuildStateContractKey(), _binarySerializer.Serialize(contract));
        }

        public async Task DeleteContract(UInt160 contractHash)
        {
            await _redisDbContext.Delete(contractHash.BuildStateContractKey());
        }

        public async Task<StorageValue> GetStorage(StorageKey key)
        {
            var raw = await _redisDbContext.Get(key.BuildStateStorageKey());
            return raw.IsNull ? null : _binarySerializer.Deserialize<StorageValue>(raw);
        }

        public async Task AddStorage(StorageKey key, StorageValue val)
        {
            await _redisDbContext.Set(key.BuildStateStorageKey(), val.Value);
        }

        public async Task DeleteStorage(StorageKey key)
        {
            await _redisDbContext.Delete(key.BuildStateStorageKey());
        }

        #endregion

        #region IRepository Index Members

        public async Task<uint> GetIndexHeight()
        {
            var val = await _redisDbContext.Get(_indexHeightKey);
            return val == RedisValue.Null ? uint.MinValue : (uint) val;
        }

        public async Task SetIndexHeight(uint height)
        {
            await _redisDbContext.Set(_indexHeightKey, height);
        }

        public async Task<HashSet<CoinReference>> GetIndexConfirmed(UInt160 scriptHash)
        {
            var raw = await _redisDbContext.Get(scriptHash.BuildIxConfirmedKey());
            return raw.IsNull ? new HashSet<CoinReference>() : _binarySerializer.Deserialize<HashSet<CoinReference>>(raw);
        }

        public async Task SetIndexConfirmed(UInt160 scriptHash, HashSet<CoinReference> coinReferences)
        {
            var raw = _binarySerializer.Serialize(coinReferences.ToArray());
            await _redisDbContext.Set(scriptHash.BuildIxConfirmedKey(), raw);
        }

        public async Task<HashSet<CoinReference>> GetIndexClaimable(UInt160 scriptHash)
        {
            var raw = await _redisDbContext.Get(scriptHash.BuildIxClaimableKey());
            return raw.IsNull ? new HashSet<CoinReference>() : _binarySerializer.Deserialize<HashSet<CoinReference>>(raw);
        }

        public async Task SetIndexClaimable(UInt160 scriptHash, HashSet<CoinReference> coinReferences)
        {
            var raw = _binarySerializer.Serialize(coinReferences.ToArray());
            await _redisDbContext.Set(scriptHash.BuildIxClaimableKey(), raw);
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _redisDbContext.Dispose();
        }

        #endregion
    }
}