﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Exceptionless.DateTimeExtensions;
using Foundatio.Repositories.Elasticsearch.Configuration;
using Foundatio.Repositories.Elasticsearch.Extensions;
using Foundatio.Repositories.Elasticsearch.Tests.Repositories.Configuration.Indexes;
using Foundatio.Repositories.Elasticsearch.Tests.Repositories.Models;
using Foundatio.Utility;
using Nest;
using Xunit;
using Xunit.Abstractions;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Foundatio.Repositories.Elasticsearch.Tests;

public sealed class IndexTests : ElasticRepositoryTestBase {
    public IndexTests(ITestOutputHelper output) : base(output) {
        Log.SetLogLevel<EmployeeRepository>(LogLevel.Warning);
    }

    public override async Task InitializeAsync() {
        await base.InitializeAsync();
        await RemoveDataAsync(false);
    }

    [Theory]
    [MemberData(nameof(AliasesDatesToCheck))]
    public async Task CanCreateDailyAliasesAsync(DateTime utcNow) {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(utcNow);
            var index = new DailyEmployeeIndex(_configuration, 1);
            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();
                IEmployeeRepository repository = new EmployeeRepository(index);

                for (int i = 0; i < 35; i += 5) {
                    var employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow.SubtractDays(i)));
                    Assert.NotNull(employee?.Id);

                    Assert.Equal(1, await index.GetCurrentVersionAsync());
                    var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(existsResponse);
                    Assert.True(existsResponse.ApiCall.Success);
                    Assert.True(existsResponse.Exists);

                    var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(aliasesResponse);
                    Assert.True(aliasesResponse.IsValid);
                    Assert.Equal(1, aliasesResponse.Indices.Count);

                    var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                    aliases.Sort();

                    Assert.Equal(GetExpectedEmployeeDailyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AliasesDatesToCheck))]
    public async Task CanCreateMonthlyAliasesAsync(DateTime utcNow) {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(utcNow);

            var index = new MonthlyEmployeeIndex(_configuration, 1);
            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();
                IEmployeeRepository repository = new EmployeeRepository(index);

                for (int i = 0; i < 4; i++) {
                    var employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow.SubtractMonths(i)));
                    Assert.NotNull(employee?.Id);

                    Assert.Equal(1, await index.GetCurrentVersionAsync());
                    var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(existsResponse);
                    Assert.True(existsResponse.ApiCall.Success);
                    Assert.True(existsResponse.Exists);

                    var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(aliasesResponse);
                    Assert.True(aliasesResponse.IsValid);
                    Assert.Equal(1, aliasesResponse.Indices.Count);

                    var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                    aliases.Sort();

                    Assert.Equal(GetExpectedEmployeeMonthlyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));
                }
            }
        }
    }

    public static IEnumerable<object[]> AliasesDatesToCheck => new List<object[]> {
        new object[] { new DateTime(2016, 2, 29, 0, 0, 0, DateTimeKind.Utc) },
        new object[] { new DateTime(2016, 8, 31, 0, 0, 0, DateTimeKind.Utc) },
        new object[] { new DateTime(2016, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
        new object[] { new DateTime(2017, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
        new object[] { new DateTime(2017, 4, 10, 18, 43, 39, 0, DateTimeKind.Utc) },
        new object[] { new DateTime(2017, 12, 31, 11, 59, 59, DateTimeKind.Utc).EndOfDay() },
        new object[] { SystemClock.UtcNow }
    }.ToArray();

    [Fact]
    public async Task GetByDateBasedIndexAsync() {
        await _configuration.DailyLogEvents.ConfigureAsync();
            
        
        // TODO: Fix this once https://github.com/elastic/elasticsearch-net/issues/3829 is fixed in beta2
        //var indexes = await _client.GetIndicesPointingToAliasAsync(_configuration.DailyLogEvents.Name);
        //Assert.Empty(indexes);

        var alias = await _client.Indices.GetAliasAsync(_configuration.DailyLogEvents.Name);
        _logger.LogRequest(alias);
        Assert.False(alias.IsValid);

        var utcNow = SystemClock.UtcNow;
        ILogEventRepository repository = new DailyLogEventRepository(_configuration);
        var logEvent = await repository.AddAsync(LogEventGenerator.Generate(createdUtc: utcNow));
        Assert.NotNull(logEvent?.Id);

        logEvent = await repository.AddAsync(LogEventGenerator.Generate(createdUtc: utcNow.SubtractDays(1)), o => o.ImmediateConsistency());
        Assert.NotNull(logEvent?.Id);

        alias = await _client.Indices.GetAliasAsync(_configuration.DailyLogEvents.Name);
        _logger.LogRequest(alias);
        Assert.True(alias.IsValid);
        Assert.Equal(2, alias.Indices.Count);

        var indexes = await _client.GetIndicesPointingToAliasAsync(_configuration.DailyLogEvents.Name);
        Assert.Equal(2, indexes.Count);

        await repository.RemoveAllAsync(o => o.ImmediateConsistency());

        Assert.Equal(0, await repository.CountAsync());
    }

    [Fact]
    public async Task MaintainWillCreateAliasOnVersionedIndexAsync() {
        var version1Index = new VersionedEmployeeIndex(_configuration, 1);
        await version1Index.DeleteAsync();

        var version2Index = new VersionedEmployeeIndex(_configuration, 2);
        await version2Index.DeleteAsync();

        // Indexes don't exist yet so the current version will be the index version.
        Assert.Equal(1, await version1Index.GetCurrentVersionAsync());
        Assert.Equal(2, await version2Index.GetCurrentVersionAsync());

        using (new DisposableAction(() => version1Index.DeleteAsync().GetAwaiter().GetResult())) {
            await version1Index.ConfigureAsync();
            Assert.True((await _client.Indices.ExistsAsync(version1Index.VersionedName)).Exists);
            Assert.Equal(1, await version1Index.GetCurrentVersionAsync());

            using (new DisposableAction(() => version2Index.DeleteAsync().GetAwaiter().GetResult())) {
                await version2Index.ConfigureAsync();
                Assert.True((await _client.Indices.ExistsAsync(version2Index.VersionedName)).Exists);
                Assert.Equal(1, await version2Index.GetCurrentVersionAsync());

                // delete all aliases
                await _configuration.Cache.RemoveAllAsync();
                await DeleteAliasesAsync(version1Index.VersionedName);
                await DeleteAliasesAsync(version2Index.VersionedName);

                await _client.Indices.RefreshAsync(Indices.All);
                var aliasesResponse = await _client.Indices.GetAliasAsync($"{version1Index.VersionedName},{version2Index.VersionedName}");
                Assert.Empty(aliasesResponse.Indices.Values.SelectMany(i => i.Aliases));

                // Indexes exist but no alias so the oldest index version will be used.
                Assert.Equal(1, await version1Index.GetCurrentVersionAsync());
                Assert.Equal(1, await version2Index.GetCurrentVersionAsync());

                await version1Index.MaintainAsync();
                aliasesResponse = await _client.Indices.GetAliasAsync(version1Index.VersionedName);
                Assert.Equal(1, aliasesResponse.Indices.Single().Value.Aliases.Count);
                aliasesResponse = await _client.Indices.GetAliasAsync(version2Index.VersionedName);
                Assert.Equal(0, aliasesResponse.Indices.Single().Value.Aliases.Count);

                Assert.Equal(1, await version1Index.GetCurrentVersionAsync());
                Assert.Equal(1, await version2Index.GetCurrentVersionAsync());
            }
        }
    }

    [Fact]
    public async Task MaintainWillCreateAliasesOnTimeSeriesIndexAsync() {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(SystemClock.UtcNow);
            var version1Index = new DailyEmployeeIndex(_configuration, 1);
            await version1Index.DeleteAsync();

            var version2Index = new DailyEmployeeIndex(_configuration, 2);
            await version2Index.DeleteAsync();

            // Indexes don't exist yet so the current version will be the index version.
            Assert.Equal(1, await version1Index.GetCurrentVersionAsync());
            Assert.Equal(2, await version2Index.GetCurrentVersionAsync());

            using (new DisposableAction(() => version1Index.DeleteAsync().GetAwaiter().GetResult())) {
                await version1Index.ConfigureAsync();
                await version1Index.EnsureIndexAsync(SystemClock.UtcNow);
                Assert.True((await _client.Indices.ExistsAsync(version1Index.GetVersionedIndex(SystemClock.UtcNow))).Exists);
                Assert.Equal(1, await version1Index.GetCurrentVersionAsync());

                // delete all aliases
                await _configuration.Cache.RemoveAllAsync();
                await DeleteAliasesAsync(version1Index.GetVersionedIndex(SystemClock.UtcNow));

                using (new DisposableAction(() => version2Index.DeleteAsync().GetAwaiter().GetResult())) {
                    await version2Index.ConfigureAsync();
                    await version2Index.EnsureIndexAsync(SystemClock.UtcNow);
                    Assert.True((await _client.Indices.ExistsAsync(version2Index.GetVersionedIndex(SystemClock.UtcNow))).Exists);
                    Assert.Equal(2, await version2Index.GetCurrentVersionAsync());

                    // delete all aliases
                    await _configuration.Cache.RemoveAllAsync();
                    await DeleteAliasesAsync(version2Index.GetVersionedIndex(SystemClock.UtcNow));

                    await _client.Indices.RefreshAsync(Indices.All);
                    var aliasesResponse = await _client.Indices.GetAliasAsync($"{version1Index.GetVersionedIndex(SystemClock.UtcNow)},{version2Index.GetVersionedIndex(SystemClock.UtcNow)}");
                    Assert.Empty(aliasesResponse.Indices.Values.SelectMany(i => i.Aliases));

                    // Indexes exist but no alias so the oldest index version will be used.
                    Assert.Equal(1, await version1Index.GetCurrentVersionAsync());
                    Assert.Equal(1, await version2Index.GetCurrentVersionAsync());

                    await version1Index.MaintainAsync();
                    aliasesResponse = await _client.Indices.GetAliasAsync(version1Index.GetVersionedIndex(SystemClock.UtcNow));
                    Assert.Equal(version1Index.Aliases.Count + 1, aliasesResponse.Indices.Single().Value.Aliases.Count);
                    aliasesResponse = await _client.Indices.GetAliasAsync(version2Index.GetVersionedIndex(SystemClock.UtcNow));
                    Assert.Equal(0, aliasesResponse.Indices.Single().Value.Aliases.Count);

                    Assert.Equal(1, await version1Index.GetCurrentVersionAsync());
                    Assert.Equal(1, await version2Index.GetCurrentVersionAsync());
                }
            }
        }
    }

    private async Task DeleteAliasesAsync(string index) {
        var aliasesResponse = await _client.Indices.GetAliasAsync(index);
        var aliases = aliasesResponse.Indices.Single(a => a.Key == index).Value.Aliases.Select(s => s.Key).ToList();
        foreach (string alias in aliases) {
            await _client.Indices.DeleteAliasAsync(new DeleteAliasRequest(index, alias));
        }
    }

    [Fact]
    public async Task MaintainDailyIndexesAsync() {
        using (TestSystemClock.Install()) {
            var index = new DailyEmployeeIndex(_configuration, 1);
            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();
                IEmployeeRepository repository = new EmployeeRepository(index);

                TestSystemClock.SetFrozenTime(DateTime.UtcNow.Subtract(TimeSpan.FromDays(15)));
                var employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: SystemClock.UtcNow), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                await index.MaintainAsync();
                Assert.Equal(1, await index.GetCurrentVersionAsync());
                var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeDailyAliases(index, SystemClock.UtcNow, employee.CreatedUtc), String.Join(", ", aliases));

                TestSystemClock.SetFrozenTime(DateTime.UtcNow.Subtract(TimeSpan.FromDays(9)));
                index.MaxIndexAge = TimeSpan.FromDays(10);
                await index.MaintainAsync();
                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeDailyAliases(index, SystemClock.UtcNow, employee.CreatedUtc), String.Join(", ", aliases));

                TestSystemClock.SetFrozenTime(DateTime.UtcNow);
                await index.MaintainAsync();
                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.False(existsResponse.Exists);

                aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.False(aliasesResponse.IsValid);
            }
        }
    }

    [Fact]
    public async Task MaintainMonthlyIndexesAsync() {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(new DateTime(2016, 8, 31, 0, 0, 0, DateTimeKind.Utc));
        var index = new MonthlyEmployeeIndex(_configuration, 1) {
            MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.SubtractMonths(4).StartOfMonth()
        };
        await index.DeleteAsync();

        var utcNow = SystemClock.UtcNow;
            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();
                IEmployeeRepository repository = new EmployeeRepository(index);

                for (int i = 0; i < 4; i++) {
                    var created = utcNow.SubtractMonths(i);
                    var employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: created));
                    Assert.NotNull(employee?.Id);

                    Assert.Equal(1, await index.GetCurrentVersionAsync());
                    var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(existsResponse);
                    Assert.True(existsResponse.ApiCall.Success);
                    Assert.True(existsResponse.Exists);

                    var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(aliasesResponse);
                    Assert.True(aliasesResponse.IsValid);
                    Assert.Equal(1, aliasesResponse.Indices.Count);

                    var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                    aliases.Sort();

                    Assert.Equal(GetExpectedEmployeeMonthlyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));
                }

                await index.MaintainAsync();

                for (int i = 0; i < 4; i++) {
                    var created = utcNow.SubtractMonths(i);
                    var employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: created));
                    Assert.NotNull(employee?.Id);

                    Assert.Equal(1, await index.GetCurrentVersionAsync());
                    var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(existsResponse);
                    Assert.True(existsResponse.ApiCall.Success);
                    Assert.True(existsResponse.Exists);

                    var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                    _logger.LogRequest(aliasesResponse);
                    Assert.True(aliasesResponse.IsValid);
                    Assert.Equal(1, aliasesResponse.Indices.Count);

                    var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                    aliases.Sort();

                    Assert.Equal(GetExpectedEmployeeMonthlyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));
                }
            }
        }
    }

    [Fact]
    public async Task MaintainOnlyOldIndexesAsync() {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(SystemClock.UtcNow.EndOfYear());

            var index = new MonthlyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.SubtractMonths(12).StartOfMonth()
            };

            await index.EnsureIndexAsync(SystemClock.UtcNow.SubtractMonths(12));
            var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(SystemClock.UtcNow.SubtractMonths(12)));
            _logger.LogRequest(existsResponse);
            Assert.True(existsResponse.ApiCall.Success);
            Assert.True(existsResponse.Exists);

            index.MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.StartOfMonth();

            await index.MaintainAsync();
            existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(SystemClock.UtcNow.SubtractMonths(12)));
            _logger.LogRequest(existsResponse);
            Assert.True(existsResponse.ApiCall.Success);
            Assert.False(existsResponse.Exists);
        }
    }

    [Fact]
    public async Task CanCreateAndDeleteIndex() {
        var index = new EmployeeIndex(_configuration);

        await index.ConfigureAsync();
        var existsResponse = await _client.Indices.ExistsAsync(index.Name);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.True(existsResponse.Exists);

        await index.DeleteAsync();
        existsResponse = await _client.Indices.ExistsAsync(index.Name);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.False(existsResponse.Exists);
    }

    [Fact]
    public async Task CanChangeIndexSettings() {
        var index1 = new VersionedEmployeeIndex(_configuration, 1, i => i
            .Settings(s => s
                .NumberOfReplicas(0)
                .Setting("index.mapping.total_fields.limit", 2000)
                .Analysis(a => a.Analyzers(a1 => a1.Custom("custom1", c => c.Filters("uppercase").Tokenizer("whitespace"))))
            ));
        await index1.DeleteAsync();

        await index1.ConfigureAsync();
        var settings = await _client.Indices.GetSettingsAsync(index1.VersionedName);
        Assert.Equal(0, settings.Indices[index1.VersionedName].Settings.NumberOfReplicas);
        Assert.NotNull(settings.Indices[index1.VersionedName].Settings.Analysis.Analyzers["custom1"]);
        
        var index2 = new VersionedEmployeeIndex(_configuration, 1, i => i.Settings(s => s
            .NumberOfReplicas(1)
            .Setting("index.mapping.total_fields.limit", 3000)
            .Analysis(a => a.Analyzers(a1 => a1.Custom("custom1", c => c.Filters("uppercase").Tokenizer("whitespace")).Custom("custom2", c => c.Filters("uppercase").Tokenizer("whitespace"))))
        ));
        
        await index2.ConfigureAsync();
        settings = await _client.Indices.GetSettingsAsync(index1.VersionedName);
        Assert.Equal(1, settings.Indices[index1.VersionedName].Settings.NumberOfReplicas);
        Assert.NotNull(settings.Indices[index1.VersionedName].Settings.Analysis.Analyzers["custom1"]);
    }

    [Fact]
    public async Task CanAddIndexMappings() {
        var index1 = new VersionedEmployeeIndex(_configuration, 1, null, m => m.Properties(p => p.Keyword(k => k.Name(n => n.EmailAddress))));
        await index1.DeleteAsync();

        await index1.ConfigureAsync();
        var fieldMapping = await _client.Indices.GetFieldMappingAsync<Employee>("emailAddress", d => d.Index(index1.VersionedName));
        Assert.NotNull(fieldMapping.Indices[index1.VersionedName].Mappings["emailAddress"]);
        
        var index2 = new VersionedEmployeeIndex(_configuration, 1, null, m => m.Properties(p => p.Keyword(k => k.Name(n => n.EmailAddress)).Number(k => k.Name(n => n.Age))));
        
        await index2.ConfigureAsync();
        fieldMapping = await _client.Indices.GetFieldMappingAsync<Employee>("age", d => d.Index(index2.VersionedName));
        Assert.NotNull(fieldMapping.Indices[index2.VersionedName].Mappings["age"]);
    }

    [Fact]
    public async Task WillWarnWhenAttemptingToChangeFieldMappingType() {
        var index1 = new VersionedEmployeeIndex(_configuration, 1, null, m => m.Properties(p => p.Keyword(k => k.Name(n => n.EmailAddress))));
        await index1.DeleteAsync();

        await index1.ConfigureAsync();
        var existsResponse = await _client.Indices.ExistsAsync(index1.VersionedName);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.True(existsResponse.Exists);
        
        var index2 = new VersionedEmployeeIndex(_configuration, 1, null, m => m.Properties(p => p.Number(k => k.Name(n => n.EmailAddress))));
        
        await index2.ConfigureAsync();
        Assert.Contains(Log.LogEntries, l => l.LogLevel == LogLevel.Error && l.Message.Contains("requires a new index version"));
    }

    [Fact]
    public async Task CanCreateAndDeleteVersionedIndex() {
        var index = new VersionedEmployeeIndex(_configuration, 1);
        await index.DeleteAsync();

        await index.ConfigureAsync();
        var existsResponse = await _client.Indices.ExistsAsync(index.VersionedName);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.True(existsResponse.Exists);

        await _client.AssertSingleIndexAlias(index.VersionedName, index.Name);

        await index.DeleteAsync();
        existsResponse = await _client.Indices.ExistsAsync(index.VersionedName);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.False(existsResponse.Exists);

        Assert.Equal(0, await _client.GetAliasIndexCount(index.Name));
    }

    [Fact]
    public async Task CanCreateAndDeleteDailyIndex() {
        var index = new DailyEmployeeIndex(_configuration, 1);
        await index.DeleteAsync();

        await index.ConfigureAsync();
        var todayDate = SystemClock.Now;
        var yesterdayDate = SystemClock.Now.SubtractDays(1);
        string todayIndex = index.GetIndex(todayDate);
        string yesterdayIndex = index.GetIndex(yesterdayDate);

        await index.EnsureIndexAsync(todayDate);
        await index.EnsureIndexAsync(yesterdayDate);

        var existsResponse = await _client.Indices.ExistsAsync(todayIndex);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.True(existsResponse.Exists);

        existsResponse = await _client.Indices.ExistsAsync(yesterdayIndex);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.True(existsResponse.Exists);

        await index.DeleteAsync();

        existsResponse = await _client.Indices.ExistsAsync(todayIndex);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.False(existsResponse.Exists);

        existsResponse = await _client.Indices.ExistsAsync(yesterdayIndex);
        _logger.LogRequest(existsResponse);
        Assert.True(existsResponse.ApiCall.Success);
        Assert.False(existsResponse.Exists);
    }

    [Fact]
    public async Task MaintainOnlyOldIndexesWithNoExistingAliasesAsync() {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(SystemClock.UtcNow.EndOfYear());

            var index = new MonthlyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.SubtractMonths(12).StartOfMonth()
            };

            await index.EnsureIndexAsync(SystemClock.UtcNow.SubtractMonths(12));
            var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(SystemClock.UtcNow.SubtractMonths(12)));
            _logger.LogRequest(existsResponse);
            Assert.True(existsResponse.ApiCall.Success);
            Assert.True(existsResponse.Exists);

            index.MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.StartOfMonth();
            await DeleteAliasesAsync(index.GetVersionedIndex(SystemClock.UtcNow.SubtractMonths(12)));

            await index.MaintainAsync();
            existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(SystemClock.UtcNow.SubtractMonths(12)));
            _logger.LogRequest(existsResponse);
            Assert.True(existsResponse.ApiCall.Success);
            Assert.False(existsResponse.Exists);
        }
    }

    [Fact]
    public async Task MaintainOnlyOldIndexesWithPartialAliasesAsync() {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(SystemClock.UtcNow.EndOfYear());

            var index = new MonthlyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.SubtractMonths(12).StartOfMonth()
            };

            await index.EnsureIndexAsync(SystemClock.UtcNow.SubtractMonths(11));
            await index.EnsureIndexAsync(SystemClock.UtcNow.SubtractMonths(12));
            var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(SystemClock.UtcNow.SubtractMonths(12)));
            _logger.LogRequest(existsResponse);
            Assert.True(existsResponse.ApiCall.Success);
            Assert.True(existsResponse.Exists);

            index.MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.StartOfMonth();
            await DeleteAliasesAsync(index.GetVersionedIndex(SystemClock.UtcNow.SubtractMonths(12)));

            await index.MaintainAsync();
            existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(SystemClock.UtcNow.SubtractMonths(12)));
            _logger.LogRequest(existsResponse);
            Assert.True(existsResponse.ApiCall.Success);
            Assert.False(existsResponse.Exists);
        }
    }

    [Theory]
    [MemberData(nameof(AliasesDatesToCheck))]
    public async Task DailyAliasMaxAgeAsync(DateTime utcNow) {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(utcNow);
            var index = new DailyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = TimeSpan.FromDays(45)
            };

            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();
                IEmployeeRepository version1Repository = new EmployeeRepository(index);

                var employee = await version1Repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeDailyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));

                employee = await version1Repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow.SubtractDays(2)), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeDailyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));

                employee = await version1Repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow.SubtractDays(35)), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeDailyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));
            }
        }
    }

    [Theory]
    [MemberData(nameof(AliasesDatesToCheck))]
    public async Task MonthlyAliasMaxAgeAsync(DateTime utcNow) {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(utcNow);

            var index = new MonthlyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = TimeSpan.FromDays(90)
            };
            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();
                IEmployeeRepository repository = new EmployeeRepository(index);

                var employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                var aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                var aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeMonthlyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));

                employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow.SubtractDays(2)), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeMonthlyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));

                employee = await repository.AddAsync(EmployeeGenerator.Generate(createdUtc: utcNow.SubtractDays(35)), o => o.ImmediateConsistency());
                Assert.NotNull(employee?.Id);

                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                aliasesResponse = await _client.Indices.GetAliasAsync(index.GetIndex(employee.CreatedUtc));
                _logger.LogRequest(aliasesResponse);
                Assert.True(aliasesResponse.IsValid);
                Assert.Equal(1, aliasesResponse.Indices.Count);
                aliases = aliasesResponse.Indices.Values.Single().Aliases.Select(s => s.Key).ToList();
                aliases.Sort();
                Assert.Equal(GetExpectedEmployeeMonthlyAliases(index, utcNow, employee.CreatedUtc), String.Join(", ", aliases));
            }
        }
    }

    [Theory]
    [MemberData(nameof(AliasesDatesToCheck))]
    public async Task DailyIndexMaxAgeAsync(DateTime utcNow) {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(utcNow);

            var index = new DailyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = TimeSpan.FromDays(1)
            };
            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();

                await index.EnsureIndexAsync(utcNow);
                var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(utcNow));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                await index.EnsureIndexAsync(utcNow.SubtractDays(1));
                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(utcNow.SubtractDays(1)));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                await Assert.ThrowsAsync<ArgumentException>(async () => await index.EnsureIndexAsync(utcNow.SubtractDays(2)));
                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(utcNow.SubtractDays(2)));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.False(existsResponse.Exists);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AliasesDatesToCheck))]
    public async Task MonthlyIndexMaxAgeAsync(DateTime utcNow) {
        using (TestSystemClock.Install()) {
            TestSystemClock.SetFrozenTime(utcNow);

            var index = new MonthlyEmployeeIndex(_configuration, 1) {
                MaxIndexAge = SystemClock.UtcNow.EndOfMonth() - SystemClock.UtcNow.StartOfMonth()
            };
            await index.DeleteAsync();

            using (new DisposableAction(() => index.DeleteAsync().GetAwaiter().GetResult())) {
                await index.ConfigureAsync();

                await index.EnsureIndexAsync(utcNow);
                var existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(utcNow));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                await index.EnsureIndexAsync(utcNow.Subtract(index.MaxIndexAge.GetValueOrDefault()));
                existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(utcNow.Subtract(index.MaxIndexAge.GetValueOrDefault())));
                _logger.LogRequest(existsResponse);
                Assert.True(existsResponse.ApiCall.Success);
                Assert.True(existsResponse.Exists);

                var endOfTwoMonthsAgo = utcNow.SubtractMonths(2).EndOfMonth();
                if (utcNow - endOfTwoMonthsAgo >= index.MaxIndexAge.GetValueOrDefault()) {
                    await Assert.ThrowsAsync<ArgumentException>(async () => await index.EnsureIndexAsync(endOfTwoMonthsAgo));
                    existsResponse = await _client.Indices.ExistsAsync(index.GetIndex(endOfTwoMonthsAgo));
                    _logger.LogRequest(existsResponse);
                    Assert.True(existsResponse.ApiCall.Success);
                    Assert.False(existsResponse.Exists);
                }
            }
        }
    }

    private static string GetExpectedEmployeeDailyAliases(IIndex index, DateTime utcNow, DateTime indexDateUtc) {
        double totalDays = utcNow.Date.Subtract(indexDateUtc.Date).TotalDays;
        var aliases = new List<string> { index.Name, index.GetIndex(indexDateUtc) };
        if (totalDays <= 30)
            aliases.Add($"{index.Name}-last30days");
        if (totalDays <= 7)
            aliases.Add($"{index.Name}-last7days");
        if (totalDays <= 1)
            aliases.Add($"{index.Name}-today");

        aliases.Sort();
        return String.Join(", ", aliases);
    }

    private static string GetExpectedEmployeeMonthlyAliases(IIndex index, DateTime utcNow, DateTime indexDateUtc) {
        var aliases = new List<string> { index.Name, index.GetIndex(indexDateUtc) };
        if (new DateTimeRange(utcNow.SubtractDays(1).StartOfMonth(), utcNow.EndOfMonth()).Contains(indexDateUtc))
            aliases.Add($"{index.Name}-today");

        if (new DateTimeRange(utcNow.SubtractDays(7).StartOfMonth(), utcNow.EndOfMonth()).Contains(indexDateUtc))
            aliases.Add($"{index.Name}-last7days");

        if (new DateTimeRange(utcNow.SubtractDays(30).StartOfMonth(), utcNow.EndOfMonth()).Contains(indexDateUtc))
            aliases.Add($"{index.Name}-last30days");

        if (new DateTimeRange(utcNow.SubtractDays(60).StartOfMonth(), utcNow.EndOfMonth()).Contains(indexDateUtc))
            aliases.Add($"{index.Name}-last60days");

        aliases.Sort();
        return String.Join(", ", aliases);
    }
}
