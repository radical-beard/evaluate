" Evaluate `.evt`: a Lua body with a leading `--- … ---` YAML frontmatter block.
" The body is highlighted as Lua; the frontmatter is overlaid as YAML.
if exists("b:current_syntax")
  finish
endif

" Body: load the full Lua syntax at the top level, so everything below the frontmatter
" highlights as Lua.
runtime! syntax/lua.vim
unlet! b:current_syntax

" Frontmatter: pull in YAML under a private cluster and overlay it on the leading block.
syntax include @evtYaml syntax/yaml.vim
unlet! b:current_syntax

" The `--- … ---` signature at the very top. `keepend` stops contained YAML items from
" swallowing the closing fence; defining it after the Lua load gives it priority over
" Lua's `--` comment match on the fence lines.
syntax region evtFrontmatter matchgroup=evtFrontmatterFence
  \ start=/\%^---$/ end=/^---$/ keepend contains=@evtYaml

highlight default link evtFrontmatterFence Delimiter

let b:current_syntax = "evt"
