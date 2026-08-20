from __future__ import annotations
import argparse,json,sys
from pathlib import Path
from att_engine.cancellation import OperationCancelledError
from att_engine.errors import AttError
from att_engine.model_service import download,safe_delete,validate
from att_engine.progress import emit

def main() -> int:
    parser=argparse.ArgumentParser();parser.add_argument("command",choices=["download","validate","delete"]);parser.add_argument("--model",required=True);parser.add_argument("--model-dir",required=True,type=Path);parser.add_argument("--cancel-file",type=Path);args=parser.parse_args()
    try:
        if args.command=="download": download(args.model_dir,args.model,args.cancel_file)
        elif args.command=="validate": emit("model_validation",model=args.model,**validate(args.model_dir,args.model,args.cancel_file))
        else: safe_delete(args.model_dir,args.model);emit("model_deleted",model=args.model,message="モデルを削除しました")
        return 0
    except OperationCancelledError: emit("cancelled",code="ATT_MODEL_DOWNLOAD_CANCELLED",message="モデル操作をキャンセルしました");return 2
    except AttError as error: emit("error",code=error.code,message=str(error));return 1
if __name__=="__main__":sys.exit(main())
