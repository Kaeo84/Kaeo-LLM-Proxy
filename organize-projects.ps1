Push-Location 'E:\GitRepos\Kaeo LLM Proxy\src'

# ========== Infrastructure: 8 flat files ==========
$infra = 'Kaeo LLM Proxy Infrastructure'
@{
	'Data'    = @('AppDatabase.cs')
	'Logging' = @('AppLogger.cs','SystemLogDbSink.cs','SystemLogSink.cs')
	'Upstream'= @('UpstreamModelMetadata.cs','UpstreamUriHelper.cs')
	'Help'    = @('HelpPages.cs')
	'Utilities'= @('PooledCharBuffer.cs')
}.GetEnumerator() | ForEach-Object {
	$dir = Join-Path $infra $_.Key; New-Item -ItemType Directory -Path $dir -Force | Out-Null
	$_.Value | ForEach-Object { $f = Join-Path $infra $_; if (Test-Path $f) { Move-Item $f $dir -Force; Write-Host "moved $_ -> $($_.Key)/" } }
}

# ========== Services: 8 flat files ==========
$svc = 'Kaeo LLM Proxy Services'
@{
	'Proxy'     = @('OllamaProxyHandler.cs','ProxyServer.cs')
	'Compaction'= @('AutoCompactionService.cs')
	'Monitoring'= @('PerformanceService.cs','StatisticsService.cs','StreamMonitor.cs')
	'Prompt'    = @('SystemPromptComposer.cs')
	'Debug'     = @('DebugNotes.cs')
}.GetEnumerator() | ForEach-Object {
	$dir = Join-Path $svc $_.Key; New-Item -ItemType Directory -Path $dir -Force | Out-Null
	$_.Value | ForEach-Object { $f = Join-Path $svc $_; if (Test-Path $f) { Move-Item $f $dir -Force; Write-Host "moved $_ -> $($_.Key)/" } }
}

# ========== Core: 1 flat file ==========
$core = 'Kaeo LLM Proxy Core'
$cfgDir = Join-Path $core 'Configuration'; New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
$f = Join-Path $core 'AppSettings.cs'; if (Test-Path $f) { Move-Item $f $cfgDir -Force; Write-Host 'moved AppSettings.cs -> Configuration/' }

# ========== Tests: 41 flat files into feature buckets ==========
$tests = 'Kaeo LLM Proxy.Tests'
@{
	'Compaction'   = @('CompactBehaviorTests.cs','CompactEndpointTests.cs','CompactionConcurrencyTests.cs',
					   'CompactionKeepAliveTests.cs','CompactionMessageRoleTests.cs','CompactionSettingsTests.cs',
					   'CompactionTargetResolutionTests.cs','ContextCompactionTests.cs','ContextSummarizeRedirectTests.cs',
					   'CopilotCompactionFormatTests.cs','CopilotCompactionTests.cs')
	'OpenAi'       = @('OllamaIrParityTests.cs','OpenAiSpecConformanceTests.cs','OpenAiSseFrameParityTests.cs',
					   'OpenAiStreamTerminatorTests.cs','OpenAiTranslationTests.cs','RequestPayloadParityTests.cs',
					   'MeaiAdapterTests.cs')
	'ModelMapping' = @('CopilotDetectionTests.cs','HiddenModelDiscoveryTests.cs','ModelMappingCloneTests.cs',
					   'ModelMappingExtractionTests.cs','ReasoningEffortNormalizationTests.cs',
					   'StreamOptionsNormalizationTests.cs','ThinkReasoningEffortMappingTests.cs',
					   'ReportedContextWindowTests.cs')
	'Logging'      = @('CrashLoggingTests.cs','HeaderLogFormattingTests.cs','RequestBodyRedactionTests.cs',
					   'RequestLoggingCoverageTests.cs','RequestLogQueryTests.cs','SystemLogQueryTests.cs')
	'Schema'       = @('AppDatabaseSchemaTests.cs','SeedDataTests.cs','RealDatabaseVerificationTests.cs')
	'Prompt'       = @('SystemPromptComposerTests.cs','DebugNotesTests.cs')
	'Passthrough'  = @('PassthroughFailureReportingTests.cs','PassthroughToolCallExtractionTests.cs')
	'Utilities'    = @('AppLoggerCollection.cs','ParityStaging.cs')
}.GetEnumerator() | ForEach-Object {
	$dir = Join-Path $tests $_.Key; New-Item -ItemType Directory -Path $dir -Force | Out-Null
	$_.Value | ForEach-Object { $f = Join-Path $tests $_; if (Test-Path $f) { Move-Item $f $dir -Force; Write-Host "moved $_ -> $($_.Key)/" } }
}

# ========== VS Extension: Polyfills.cs -> Utilities/ (leave VsPackage.cs at root) ==========
$vs = 'Kaeo VS Extension'
$utilDir = Join-Path $vs 'Utilities'; New-Item -ItemType Directory -Path $utilDir -Force | Out-Null
$f = Join-Path $vs 'Polyfills.cs'; if (Test-Path $f) { Move-Item $f $utilDir -Force; Write-Host 'moved Polyfills.cs -> Utilities/' }

Write-Host ''
Write-Host '=== Verification ==='
$projects = @('Kaeo LLM Proxy','Kaeo LLM Proxy Core','Kaeo LLM Proxy Infrastructure','Kaeo LLM Proxy Services','Kaeo LLM Proxy VS Extension.Core','Kaeo LLM Proxy Module Code Vector Store','Kaeo LLM Proxy Module SSH','Kaeo LLM Proxy Module Web Search','Kaeo LLM Proxy.Tests','Kaeo VS Extension')
foreach ($p in $projects) {
	if (Test-Path $p) {
		$flat = Get-ChildItem $p -File -Force -Filter *.cs | Where-Object { $_.Name -notlike '*.csproj*' -and $_.Name -notlike '*.Designer.cs' }
		Write-Host "$p`: $($flat.Count) flat .cs files"
		if ($flat) { $flat | ForEach-Object { Write-Host "  $($_.Name)" } }
	}
}

Pop-Location