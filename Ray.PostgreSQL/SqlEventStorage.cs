﻿using Dapper;
using Npgsql;
using NpgsqlTypes;
using ProtoBuf;
using Ray.Core.EventSourcing;
using Ray.Core.Message;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Ray.PostgreSQL
{
    public class SqlEventStorage<K> : IEventStorage<K>, IEventFlowStorage
    {
        SqlGrainConfig tableInfo;
        public SqlEventStorage(SqlGrainConfig tableInfo)
        {
            this.tableInfo = tableInfo;
        }
        public async Task<IList<IEventBase<K>>> GetListAsync(K stateId, Int64 startVersion, Int64 endVersion, DateTime? startTime = null)
        {
            var originList = new List<SqlEvent>((int)(endVersion - startVersion));
            await Task.Run(async () =>
            {
                var tableList = await tableInfo.GetTableList(startTime);
                using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                {
                    await conn.OpenAsync();
                    foreach (var table in tableList)
                    {
                        var sql = $"COPY (SELECT typecode,data from {table.Name} WHERE stateid='{stateId.ToString()}' and version>{startVersion} and version<={endVersion} order by version asc) TO STDOUT (FORMAT BINARY)";
                        using (var reader = conn.BeginBinaryExport(sql))
                        {
                            while (reader.StartRow() != -1)
                            {
                                originList.Add(new SqlEvent { TypeCode = reader.Read<string>(NpgsqlDbType.Varchar), Data = reader.Read<byte[]>(NpgsqlDbType.Bytea) });
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);

            var list = new List<IEventBase<K>>(originList.Count);
            foreach (var origin in originList)
            {
                if (MessageTypeMapper.EventTypeDict.TryGetValue(origin.TypeCode, out var type))
                {
                    using (var ms = new MemoryStream(origin.Data))
                    {
                        if (Serializer.Deserialize(type, ms) is IEventBase<K> evt)
                        {
                            list.Add(evt);
                        }
                    }
                }
            }
            return list;
        }
        public async Task<IList<IEventBase<K>>> GetListAsync(K stateId, string typeCode, Int64 startVersion, Int32 limit, DateTime? startTime = null)
        {
            var originList = new List<byte[]>(limit);
            if (MessageTypeMapper.EventTypeDict.TryGetValue(typeCode, out var type))
            {
                await Task.Run(async () =>
                {
                    var tableList = await tableInfo.GetTableList(startTime);
                    using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                    {
                        await conn.OpenAsync();
                        foreach (var table in tableList)
                        {
                            var sql = $"COPY (SELECT data from {table.Name} WHERE stateid='{stateId.ToString()}' and typecode='{typeCode}' and version>{startVersion} order by version asc limit {limit}) TO STDOUT (FORMAT BINARY)";
                            using (var reader = conn.BeginBinaryExport(sql))
                            {
                                while (reader.StartRow() != -1)
                                {
                                    originList.Add(reader.Read<byte[]>(NpgsqlDbType.Bytea));
                                }
                            }
                            if (originList.Count >= limit)
                                break;
                        }
                    }
                }).ConfigureAwait(false);
            }
            var list = new List<IEventBase<K>>(originList.Count);
            foreach (var origin in originList)
            {
                using (var ms = new MemoryStream(origin))
                {
                    if (Serializer.Deserialize(type, ms) is IEventBase<K> evt)
                    {
                        list.Add(evt);
                    }
                }
            }
            return list;
        }

        static ConcurrentDictionary<string, string> saveSqlDict = new ConcurrentDictionary<string, string>();
        public async ValueTask<bool> SaveAsync(IEventBase<K> evt, byte[] bytes, string uniqueId = null)
        {
            var wrap = EventBytesFlowWrap<K>.Create(evt, bytes, uniqueId);
            await tableInfo.EventFlow.SendAsync(wrap);
            await TriggerFlowProcess();
            return await wrap.TaskSource.Task;
        }
        int isProcessing = 0;
        public async Task TriggerFlowProcess()
        {
            if (Interlocked.CompareExchange(ref isProcessing, 1, 0) == 0)
            {
                while (await FlowProcess()) { }
                Interlocked.Exchange(ref isProcessing, 0);
            }
        }
        private async ValueTask<bool> FlowProcess()
        {
            if (tableInfo.EventFlow.TryReceiveAll(out var firstBlock))
            {
                await Task.Delay(10);
                int counts = 0;
                var events = new List<object>(firstBlock);
                while (tableInfo.EventFlow.TryReceiveAll(out var block))
                {
                    await Task.Delay(10);
                    events.AddRange(block);
                    counts++;
                    if (counts > 5) break;
                }
                var wrapList = events.Select(wrap => wrap as EventBytesFlowWrap<K>).ToList();
                try
                {
                    var saved = await BatchSaveAsync(wrapList.Select(data => new EventSaveWrap<K>(data.Value, data.Bytes, data.UniqueId)).ToList());
                    if (saved)
                    {
                        foreach (var wrap in wrapList)
                        {
                            wrap.TaskSource.SetResult(true);
                        }
                    }
                    else
                    {
                        await ReTry(wrapList);
                    }
                }
                catch
                {
                    await ReTry(wrapList);
                }
                return true;
            }
            return false;
        }
        public async Task ReTry(List<EventBytesFlowWrap<K>> wrapList)
        {
            foreach (var data in wrapList)
            {
                try
                {
                    data.TaskSource.TrySetResult(await SingleSaveAsync(data.Value, data.Bytes, data.UniqueId));
                }
                catch (Exception e)
                {
                    data.TaskSource.TrySetException(e);
                }
            }
        }
        private async Task<bool> SingleSaveAsync(IEventBase<K> evt, byte[] bytes, string uniqueId = null)
        {
            var table = await tableInfo.GetTable(evt.Timestamp);
            if (!saveSqlDict.TryGetValue(table.Name, out var saveSql))
            {
                saveSql = $"INSERT INTO {table.Name}(stateid,uniqueId,typecode,data,version) VALUES(@StateId,@UniqueId,@TypeCode,@Data,@Version)";
                saveSqlDict.TryAdd(table.Name, saveSql);
            }
            try
            {
                using (var conn = tableInfo.CreateConnection())
                {
                    return (await conn.ExecuteAsync(saveSql, new { StateId = evt.StateId.ToString(), UniqueId = uniqueId, evt.TypeCode, Data = bytes, evt.Version })) > 0;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is PostgresException e && e.SqlState == "23505"))
                {
                    throw ex;
                }
            }
            return false;
        }
        static ConcurrentDictionary<string, string> copySaveSqlDict = new ConcurrentDictionary<string, string>();
        public async ValueTask<bool> BatchSaveAsync(List<EventSaveWrap<K>> list)
        {
            var table = await tableInfo.GetTable(DateTime.UtcNow);
            if (list.Count > 1)
            {
                if (!copySaveSqlDict.TryGetValue(table.Name, out var saveSql))
                {
                    saveSql = $"copy {table.Name}(stateid,uniqueId,typecode,data,version) FROM STDIN (FORMAT BINARY)";
                    copySaveSqlDict.TryAdd(table.Name, saveSql);
                }
                try
                {
                    await Task.Run(async () =>
                    {
                        using (var conn = tableInfo.CreateConnection() as NpgsqlConnection)
                        {
                            await conn.OpenAsync();
                            using (var writer = conn.BeginBinaryImport(saveSql))
                            {
                                foreach (var evt in list)
                                {
                                    writer.StartRow();
                                    writer.Write(evt.Evt.StateId.ToString(), NpgsqlDbType.Varchar);
                                    writer.Write(evt.UniqueId, NpgsqlDbType.Varchar);
                                    writer.Write(evt.Evt.TypeCode, NpgsqlDbType.Varchar);
                                    writer.Write(evt.Bytes, NpgsqlDbType.Bytea);
                                    writer.Write(evt.Evt.Version, NpgsqlDbType.Bigint);
                                }
                                writer.Complete();
                            }
                        }
                    }).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex)
                {
                    if (!(ex is PostgresException e && e.SqlState == "23505"))
                    {
                        throw ex;
                    }
                }
            }
            else
            {
                if (!saveSqlDict.TryGetValue(table.Name, out var saveSql))
                {
                    saveSql = $"INSERT INTO {table.Name}(stateid,uniqueId,typecode,data,version) VALUES(@StateId,@UniqueId,@TypeCode,@Data,@Version)";
                    saveSqlDict.TryAdd(table.Name, saveSql);
                }
                try
                {
                    using (var conn = tableInfo.CreateConnection())
                    {
                        return (await conn.ExecuteAsync(saveSql, list.Select(data => new { StateId = data.Evt.StateId.ToString(), data.UniqueId, data.Evt.TypeCode, Data = data.Bytes, data.Evt.Version }).ToList())) > 0;
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is PostgresException e && e.SqlState == "23505"))
                    {
                        throw ex;
                    }
                }
            }
            return false;
        }
    }
}
