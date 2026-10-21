using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 1. 설정과 상태 
// 2. 준비 Awake 부품 처음 위치를 기억 -> Start(IK 준비를 기다린 뒤 대기 위치로)
// 3. 부품 찾기 
// 4. 집어서 옮기기 
// 5. 테스트 : 부품 원래 위치로 되돌리기, OnGUI 

public class PickPlace : MonoBehaviour
{
    #region 0.설정과 준비
    [Header("필수 연결")]
    public ArmIK ik;
    public GripperController gripper;
    public Transform spawnPoint;   // 부품 리셋 위치 

    public Transform[] bins;   // 부품 넣을 통들. index로 고른다 // 0은 빨간통, 1은 파란통

    [Header("위치")]
    public Vector3 homePositioin = new Vector3(0.1f, 0.22f, -0.18f); //대기 위치 (작업끝나면 돌아오는 곳)
    public float approachHeight = 0.1f;         //부품 중심 위치 및 몇m내려갈지
    public float pickHeightOffset = 0.006f;     // 손가락이 테이블에 닿으면 올리기 
    public float binDropHeight = 0.12f;

    [Header("속도")]
    public float traverSpeed = 0.15f;   //통으로 옮길떄 속도
    public float approachSpeed = 0.05f; // 부품쪽으로 내려가고 올라올때 이동속도 

    [Header("테스트 변수")]
    public bool shpwTestButtons = true;
    public float[] yawOption = { 0, 15, 30, 45, 60, 75, 90,105, 120, 135, 150, 165 }; //15도 간격 12칸 

    public bool IsBusy { get; private set; } // 작업중인지 (작업중에는 새 명령을 받지 않기 위해서)
    public string Status { get; private set; } = "대기";  //지금단계 (화면 표시용)

    public Rigidbody LastPart { get; private set; }   //마지막으로 잡으려한 부품
    public bool LastSuccess { get; private set; }     //마지막 시도에서 잡기 성공했는지

    private int selectedYaw; // 테스트 버튼에서 고른 손목 각도 칸의 번호 (yawOption의 몇번째인지)

    Dictionary<Rigidbody, (Vector3 pos, Quaternion rot)> initialPoses = new Dictionary<Rigidbody, (Vector3 pos, Quaternion rot)>();
    #endregion

    #region 1. 준비
    private void Awake()
    {
        //ArmIK가 NULL이라면 IK연결해주기
        //반복문으로 돌면서 현재씬에있는 "Part"라는 부품 전부 찾기 GameObject.FindGameObjectsWithTag
        //부품에 붙어있는 RigidBody를 변수로 저장
        //RigidBody가 존재하면 initialPoses 딕셔러니 값을 저장
        if (ik == null)
        {
            ik=GetComponent<ArmIK>();
        }

        //GameObject.FindGameObjectsWithTag()
        foreach (GameObject go in GameObject.FindGameObjectsWithTag("Part"))
        {
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                initialPoses[rb] = (rb.transform.position, rb.transform.rotation);
            }
        }
    }

    IEnumerator Start()
    {
        IsBusy = true;
        yield return new WaitUntil(() => ik.Ready); //ik사용을 위해 ik준비 완료때까지 기다린다

        Status = "대기 위치로";

        yield return ik.MoveTo(homePositioin, 0f); //손목 0도로 대개 위치까지 이동
        Status = "대기";
        IsBusy = false;
    }

    #endregion

    #region 2.부품찾기
    // SpawnPoint에서 가장 가까운 부품을 찾는다 
    // 부품을 하나씩 보면서 거리를 재고, 지금까지 찾은 값보다 가장 가까운 것보다 가까우면 바꾼다.
    private Rigidbody FindPart()
    {
        Rigidbody best = null; //지금까지 가장 가까운 부품 
        float bestDistance = float.MaxValue;// 아주 큰값으로 시작해야 첫부품이 무조건 뽑힘

        Vector3 from = spawnPoint != null ? spawnPoint.position : transform.position;// 기준점은 spawnPoint, 없으면 로봇 위치를 기준

        foreach (GameObject go in GameObject.FindGameObjectsWithTag("Part"))
        {
            Rigidbody rb = go.GetComponentInParent<Rigidbody>();
            if (rb == null || rb == gripper.HeldPart) continue; //Rigidbody 없거나 이미 잡고있으면 건너뜀

            float d = Vector3.Distance(rb.position, from); //기준점과 부품사이의 거리

            // 더 가까운 거리를 찾았다면 변수 값 변경
            //부품값도 넣어주기
            if (d < bestDistance)    //더 가까우면 1등 교체
            {
                bestDistance = d;
                best = rb;
            }
        }
        //가장 가까운 부품을 리턴해보기 (하나도 없으면 null)
        return best;
    }
    #endregion

    #region 3. 집어서 옮기기
    //binIndex 어느통에 넣을지
    //graspYaw 몇도로 잡을지
    //graspOnly = true 면 학습용 / false = 시연용

    //학습용(ML)은 부품을 잡고 들어올리기까지 진행

    //시연용(Unity Play)은 통에 넣은 것까지 진행 

    //target 잡을 부품 ( null인 경우에는 spawnpoint에서 가장 가까운 부품)
    //동작 순서 
    //부품 위 -> 내려가기 -> 잡기(판정) -> 들어올리기 -> 통위 -> 놓기 -> 대기위치 복귀
    public IEnumerator Run(int binIndex, float graspYaw, bool graspOnly = false, Rigidbody target = null)
    {
        if (IsBusy) yield break;  //이미 작업 진행중이면 무시 

        IsBusy = true;
        LastSuccess = false;

        //잡을 부품 정하기, 전달 받은 target이 있다면 사용하고 아니면 가장 가까운 부품을 찾기
        Rigidbody part = target != null ? target : FindPart();
        LastPart = part;

        if (part == null)
        {
            Status = "부품 없음";
            IsBusy = false;
            yield break;
        }

        //시나리오 사람이 컵을 잡는다
        //1.손을 컵 위쪽으로 가져간다(above)
        //2. 수직으로 내려감
        //3. 잡는다
        //4. 다시 들어올린다(above)

        Vector3 p = part.position;//부품 중심 
        Vector3 above = p + Vector3.up * approachHeight; //부품 바로 위 10cm

        //그리퍼가 부품을 잡을때 손가락 끝이 테이블에 먼저 닿을수 있으므로 부품에서 pickHeightOffset을 적용해야함
        Vector3 pick = p + Vector3.up * pickHeightOffset; //잡는 위치는 pickHeightOffset 만큼 올라감

        //1. 부품 위로 : 큰 이동 MoveTo
        Status = $"1.부품 위로 (손목{graspYaw}도)";
        yield return ik.MoveTo(above, graspYaw);

        //2. 내려가는 단계: 잡는 위치로 내려간다. 부품을 건드리면 안되니 MoveLinear 수직 직선 이동

        Status = $"2. 내려가기";
        yield return ik.MoveLinear(pick, approachSpeed, graspYaw);
        //3. 잡기 

        Status = $"3. 잡기";
        yield return gripper.Close();

        //4. 잡기 실패
        if (gripper.HeldPart == null)  //판정에서 실패했다면 떨어졌거나 손가락이 제대로 닿지 않은경우 
        {
            Status = "잡기 실패 -{gripper.LastGraspInfo} ";//실패 이유를 그리퍼에서 받아서 표시

            //그리퍼 들어올리기
            yield return ik.MoveLinear(above, approachSpeed, graspYaw);  //들어올림

            //3. 잡기 
            //그리퍼 열기
            yield return gripper.Open();

            //학습용 모드인지 아닌지, 학습용이 아니라면 다시 대기상태로 이동 
            if (!graspOnly)
            {
                yield return ik.MoveTo(homePositioin, 0f);
            }

            IsBusy = false;
            yield break;
        }

        //5. 들어올리기 
        Status = "4.들어올리기";
        yield return ik.MoveLinear(above, approachSpeed, graspYaw);

        //들어올린 뒤에도 잡고있는지 체크, 들어올린 뒤에도 잡고있으면 성공 
        LastSuccess = gripper.HeldPart != null;

        //학습용이면 여기서 끝 (성공 여부만 확인하고 부품을 놓음)
        if (graspOnly)
        {
            Status = LastSuccess ? "학습 : 잡기 성공" : "학습: 잡기 실패";
            yield return gripper.Open();
            IsBusy = false;
            yield break ;
        }

        //6. 통위로 이동
        Status = $"5. {bins[binIndex].name} 위로";
        //bins[binIndex] 0이면 빨간통, 1이면 파란통
        Vector3 binAbove = bins[binIndex].position + Vector3.up * binDropHeight;
        yield return ik.MoveLinear(binAbove, traverSpeed, graspYaw);

        //7. 놓기 
        Status = "6. 놓기";
        yield return gripper.Open();


        //8. 복귀 (대기 위치로)
        Status = "7. 복귀";
        yield return ik.MoveTo(homePositioin, 0f);

        Status ="완료";
        IsBusy = false;

    }
    #endregion

    #region 테스트 
    //모든 부품을 play 시작때의 위치,회전으로 되돌리기
    //잡고있는 부품은 건드리지 않는다 

    private void ResetParts()
    {
        foreach(var kv in initialPoses)
        {
            Rigidbody rb = kv.Key;
            if (rb == null || rb == gripper.HeldPart) continue;

            rb.transform.SetParent(null, true);  //혹시 다른 물체의 자식이면 떼어넴

            rb.isKinematic = false;              // 물리 다시 켜시
            rb.linearVelocity = Vector3.zero;    //움직이던 속도 없애기
            rb.angularVelocity = Vector3.zero;   // 돌던 속도 없애기 
            rb.transform.SetPositionAndRotation(kv.Value.pos, kv.Value.rot);
        }

        Physics.SyncTransforms();   // 옮긴 위치를 물리엔진에 바로 반영
        Status = " 부품 리셋완료 (처음 위치, 회전값으로)";
    }

    //화면에 테스트 패널
    //상태 정보 확인, 손목 각도 고르기, Run 실행, 부품 리셋 버튼 
    // OnGUI는 매 프레임마다 실행되어서 화면을 다시 그린다
    private void OnGUI()
    {
        //테스트 버튼 표시가 꺼져있으면 화면을 그리지 않음
        if(!shpwTestButtons) return;

        //상자 영역 만들기
        //기준점 (0,0)에서 화면 왼쪽 위. x는 오른쪽으로 y는 아래커짐 
        //왼쪽에서 10, 위에서 215정도 내려온 곳에 너비 300 높이 230 상자를 만든다
        GUILayout.BeginArea(new Rect(10, 215, 300, 230), GUI.skin.box);
        GUILayout.Label($"상태: {Status}");

        //아래 버튼들의 잠금 조건: 작업중, IK없음, IK 준비가 안됐을때, IK 이동중
        GUI.enabled = !IsBusy && ik != null && ik.Ready && !ik.IsMoving;

        GUILayout.Label("손목 각도");

        //칸에 사용할 글자 만들기 0,15,30 ... -> 0도 
        var labels = new string[yawOption.Length];

        //미션
        //반복문 for로 labels를 돌면서 labels의 인덱스에 "0도"처럼 표시하게하기 
        //public float[] yawOption = { 0, 15, 30, 45, 60, 75, 106, 120, 135, 150, 165 }; //15도 간격 12칸 
        //yawOption.Length의 배열 크기는 12

        for (int i = 0; i < yawOption.Length; i++)
        {
            //labels[i] = yawOption[i].ToString("도");
            labels[i] = $"{yawOption[i]}도";
        }

        //SelectionGrid(지금 선택된 칸 번호, 칸 글자들, 한줄에 놓을 칸수)
        //12칸을 6칸씩 2줄로 그리고 , 사용자가 누른 칸 번호를 돌려준다 -> selectedYaw 저장해서 기억
        selectedYaw = GUILayout.SelectionGrid(selectedYaw, labels, 6);

        // 통마다 버튼 하나씩 만들기.
        for (int i = 0; i<bins.Length; i++) 
        {
            if (bins[i] != null && GUILayout.Button($"{bins[i].name}으로 넣기")) 
            {
                StartCoroutine(Run(i, yawOption[selectedYaw]));
            }
        }

        //부품 리셋 버튼, 부품을 처음 위치와 회전으로 돌린다
        if(GUILayout.Button("부품 리셋 (처음 위치/회전값으로)"))
        {
           ResetParts();
           
        }

        GUI.enabled = true; //잠금을 해제 (다른스크립트 GUI 사용되도록)
        GUILayout.EndArea(); //영역 끝


    }



    #endregion


}
