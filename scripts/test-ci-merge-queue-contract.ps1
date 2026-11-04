# CI 工作流契约测试（分层复检与批量落轨，设计契约 #55）：锁定 .github/workflows/ci.yml 的
# 触发面与必需检查名——①on: 必含 push/pull_request/merge_group 三触发且都锚定 main
# （merge_group＝合并队列对整批组合结果跑同一全量门）；②汇总 job 名必须恰为「格式、测试与构建」
# （main 分支保护必需检查名，CONTRIBUTING「评审与合并」约束——扩充检查时保留该名称）；
# ③windows job 产出必须被 quality 汇总门消费（needs 链不断）。文件内容断言先例＝
# test-bootstrap-*-contract.ps1。0 退出码＝通过；由选择器 CiWorkflowContract 区域
# （Kind=Script）在 Core 基线后子进程执行。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repoRoot '.github\workflows\ci.yml'
if (-not (Test-Path -LiteralPath $workflowPath)) { throw "ci.yml 不存在：$workflowPath" }
$workflow = Get-Content -LiteralPath $workflowPath -Raw -Encoding UTF8

function Assert-WorkflowContract([bool]$condition, [string]$message) {
    if (-not $condition) { throw "ci.yml 契约（#55） violated：$message" }
}

# ① 触发面：三触发齐备且锚定 main（merge_group 缺失＝合并队列组合结果无门可跑＝全卡死）。
Assert-WorkflowContract ($workflow -match '(?m)^\s*push:\s*$') 'on.push 触发缺失'
Assert-WorkflowContract ($workflow -match '(?m)^\s*pull_request:\s*$') 'on.pull_request 触发缺失'
Assert-WorkflowContract ($workflow -match '(?m)^\s*merge_group:\s*$') 'on.merge_group 触发缺失（合并队列批量落轨的组合全量门，契约 #55）'
Assert-WorkflowContract (@([regex]::Matches($workflow, '(?m)^\s*branches:\s*\[main\]\s*$')).Count -ge 3) 'push/pull_request/merge_group 三触发必须各自锚定 branches: [main]'

# ② 必需检查名：quality 汇总 job 名恰为「格式、测试与构建」（分支保护必需检查 contexts 逐字一致）。
Assert-WorkflowContract ($workflow -match '(?m)^\s*name:\s*格式、测试与构建\s*$') '汇总 job 名必须恰为「格式、测试与构建」（main 分支保护必需检查名，不得改名）'
Assert-WorkflowContract ($workflow -match '(?m)^\s{2}windows:\s*$') 'windows 产出 job 缺失（质量门双 job 结构不得拆散）'
Assert-WorkflowContract ($workflow -match '(?m)^\s{2}quality:\s*$') 'quality 汇总 job 缺失'

# ③ 汇总门消费链：quality job 必须 needs windows（always() 兜底语义不因检查扩充断裂）。
Assert-WorkflowContract ($workflow -match '(?s)quality:.*?needs:\s*\[\s*windows\s*\]') 'quality 汇总门必须 needs: [windows]（消费链断裂＝检查名存在但门失效）'
Assert-WorkflowContract ($workflow -match '(?s)quality:.*?if:\s*\$\{\{\s*always\(\)\s*\}\}') 'quality 汇总门必须保持 if: always()（windows 失败/取消/跳过都判红）'

Write-Host 'ci.yml 合并队列契约通过：三触发锚定 main；必需检查名「格式、测试与构建」不变；quality→windows 消费链完整。'
exit 0
