---
name: rule-operator-design
description: Use ao projetar ou adicionar um operador ao catálogo tipado do motor de regras (comparação, texto, números, datas, coleções, etc.). Garante que o operador segue o contrato exigido sem abrir espaço para código arbitrário.
---

# Projetar um operador do motor de regras

1. Confirme a categoria do operador (comparação, existência/nulidade, lógica
   booleana, texto, números, datas, conversão, constantes/defaults, tabela
   DE-PARA, coleções/filtros/agregações, formatação, omissão de nós).
2. Declare explicitamente, antes de implementar:
   - Tipos de entrada aceitos e tipo de retorno.
   - Parâmetros (nome, tipo, obrigatoriedade, default).
   - Validações que o operador executa e os códigos de erro que pode emitir.
   - Se é compatível com coleções (aplicável dentro de um bloco `for each`) e
     como se comporta com zero, um ou vários itens.
3. Nunca implemente um operador que aceite ou execute código-fonte arbitrário
   do usuário (JavaScript, C# dinâmico, expressões livres). Se a necessidade
   parecer exigir isso, volte para o arquiteto — é uma decisão de ADR, não uma
   implementação ad-hoc.
4. Implemente o operador de forma que ele seja adicionável por outros
   desenvolvedores sem modificar o núcleo do motor (registro via catálogo/
   extensão, não `switch` fechado).
5. Escreva testes unitários cobrindo: caminho feliz, tipo inválido, parâmetro
   ausente, comportamento com nulo, e comportamento dentro de coleção (se
   aplicável).
6. Documente o operador no catálogo (`docs/domain/` ou local equivalente) com
   um exemplo de uso real (ex.: um layout fiscal).
