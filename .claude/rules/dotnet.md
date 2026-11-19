# Regras — C# / .NET

Aplica-se a todo código em `src/` e `tests/`.

- Versão do .NET/ASP.NET Core é a LTS registrada no ADR de arquitetura
  correspondente (a decidir na Fase 1). Não misture versões entre projetos
  sem um ADR justificando.
- Direção de dependência obrigatória: `Tradux.Domain` e
  `Tradux.Engine` não referenciam `Tradux.Infrastructure.*`,
  `Tradux.Api` ou `Tradux.Web`. Se um teste de arquitetura
  (`Tradux.ArchitectureTests`) existir, ele deve falhar caso essa regra
  seja violada.
- Geração de XML é feita a partir do modelo compilado do XSD. Nunca use
  template de string, interpolação de string para montar tags, ou
  concatenação para produzir o documento final.
- Conversões de tipo (números, datas) são sempre invariant-culture, nunca
  dependentes de `CultureInfo.CurrentCulture`.
- Operadores do motor de regras são registrados via catálogo extensível — não
  adicione um novo operador como caso de `switch`/`if` fechado dentro do
  motor.
- Nenhuma dependência de `Tradux.Domain`/`Tradux.Engine` em
  bibliotecas de acesso a rede, banco de dados ou UI.
- Erros de XSD, conversão, cardinalidade, compilação e execução são sempre
  tratados como críticos no modelo de erro do motor — não os rebaixe para
  aviso silenciosamente.
