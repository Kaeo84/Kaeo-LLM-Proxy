Push-Location 'E:\GitRepos\Kaeo LLM Proxy\src'
$projects = @(
	'Kaeo LLM Proxy',
	'Kaeo LLM Proxy Core',
	'Kaeo LLM Proxy Infrastructure',
	'Kaeo LLM Proxy Services',
	'Kaeo LLM Proxy VS Extension.Core',
	'Kaeo LLM Proxy Module Code Vector Store',
	'Kaeo LLM Proxy Module SSH',
	'Kaeo LLM Proxy Module Web Search',
	'Kaeo LLM Proxy.Tests',
	'Kaeo VS Extension'
)
foreach ($p in $projects) {
	if (-not (Test-Path $p)) { continue }
	Write-Host "`n===== $p ====="
	$files = Get-ChildItem $p -Recurse -File -Force | Where-Object {
		$_.FullName -notmatch '\\(bin|obj)\\|\\.csproj\.user$|\\.vs\\'
	}
	# Group by directory relative to project root
	$files | ForEach-Object {
		$rel = $_.DirectoryName.Replace("$($pwd)\$p", '').Replace('\', '/')
		if ($rel -eq '') { $rel = '(root)' }
		"$rel  ->  $($_.Name)"
	} | Sort-Object
}
Pop-Location